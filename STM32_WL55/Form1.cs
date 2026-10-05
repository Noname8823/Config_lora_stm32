using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace STM32_WL55
{
    public partial class Form1 : Form
    {
        // =====================================================
        // PROTOCOL CONFIGURATION
        // =====================================================

        private const byte CMD_PING = 0x01;
        private const byte CMD_GET_CFG = 0x02;
        private const byte CMD_SET_CFG = 0x03;
        private const byte CMD_SAVE = 0x04;
        private const byte CMD_GET_INPUT = 0x05;
        private const byte CMD_REBOOT = 0x06;

        // Broadcast destination
        private const byte BROADCAST_ID = 0xFF;

        private const int BAUDRATE = 9600;
        private const int TIMEOUT_MS = 5000;

        // Retry: chỉ dùng cho lệnh đọc (idempotent)
        private const int READ_RETRIES = 2;

        // Giới hạn bộ nhớ
        private const int MAX_RX_BUFFER = 512;
        private const int MAX_LOG_CHARS = 100000;
        private const int MAX_LOG_LINES_PER_TICK = 200;
        private const int MAX_RAW_LOG_BYTES = 32;

        private static readonly byte[] MAGIC =
        {
            0xC3, 0x3C, 0xA5, 0x5A
        };


        // =====================================================
        // SERIAL PORT
        // =====================================================

        private readonly SerialPort serial = new SerialPort();

        private readonly object rxLock = new object();

        private readonly SemaphoreSlim commandLock = new SemaphoreSlim(1, 1);

        private readonly List<byte> rxBuffer = new List<byte>();

        private TaskCompletionSource<byte[]> pendingReply;

        private byte expectedResponse;

        private volatile bool closing = false;


        // =====================================================
        // LOG (QUEUE + TIMER, KHÔNG BeginInvoke TỪNG DÒNG)
        // =====================================================

        private readonly ConcurrentQueue<string> logQueue =
            new ConcurrentQueue<string>();

        private readonly System.Windows.Forms.Timer logTimer =
            new System.Windows.Forms.Timer { Interval = 50 };


        // =====================================================
        // CONSTRUCTOR
        // =====================================================

        public Form1()
        {
            InitializeComponent();

            LoadDefaultValues();

            RefreshComPorts();

            serial.DataReceived += Serial_DataReceived;

            logTimer.Tick += LogTimer_Tick;
            logTimer.Start();


            // Register button events

            btnRefresh.Click -= BtnRefresh_Click;
            btnRefresh.Click += BtnRefresh_Click;

            btnConnect.Click -= BtnConnect_Click;
            btnConnect.Click += BtnConnect_Click;

            btnTestAT.Click -= BtnTestAT_Click;
            btnTestAT.Click += BtnTestAT_Click;

            btnGetConfig.Click -= BtnGetConfig_Click;
            btnGetConfig.Click += BtnGetConfig_Click;

            btnApply.Click -= BtnApply_Click;
            btnApply.Click += BtnApply_Click;

            btnSave.Click -= BtnSave_Click;
            btnSave.Click += BtnSave_Click;

            btnGetInput.Click -= BtnGetInput_Click;
            btnGetInput.Click += BtnGetInput_Click;


            btnTestAT.Text = "Test / PING";

            btnSave.Text = "Save Flash";

            UpdateConnectionUI();

            Log("LoRa Configuration Tool started.");

            Log("Mode: BROADCAST (DST = 0xFF).");
        }


        // =====================================================
        // LOAD DEFAULT VALUES
        // =====================================================

        private void LoadDefaultValues()
        {
            // UART
            cmbBaud.Items.Clear();
            cmbBaud.Items.Add("9600");
            cmbBaud.SelectedIndex = 0;
            cmbBaud.Enabled = false;

            // NODE ID
            numNodeId.Minimum = 1;
            numNodeId.Maximum = 254;
            numNodeId.Value = 1;

            // TX POWER
            numPower.Minimum = -9;
            numPower.Maximum = 14;
            numPower.Value = 14;

            // FREQUENCY
            Populate(
                cmbFreq,
                new OptionItem("433 MHz", 433000000),
                new OptionItem("470 MHz", 470000000),
                new OptionItem("868 MHz", 868000000),
                new OptionItem("915 MHz", 915000000)
            );

            // BANDWIDTH
            Populate(
                cmbBw,
                new OptionItem("125 kHz", 0),
                new OptionItem("250 kHz", 1),
                new OptionItem("500 kHz", 2)
            );

            // SPREADING FACTOR
            Populate(
                cmbSf,
                new OptionItem("SF7", 7),
                new OptionItem("SF8", 8),
                new OptionItem("SF9", 9),
                new OptionItem("SF10", 10),
                new OptionItem("SF11", 11),
                new OptionItem("SF12", 12)
            );

            // CODING RATE
            Populate(
                cmbCr,
                new OptionItem("4/5", 1),
                new OptionItem("4/6", 2),
                new OptionItem("4/7", 3),
                new OptionItem("4/8", 4)
            );

            // LOG
            txtLog.Multiline = true;
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
        }


        private static void Populate(
            ComboBox combo,
            params OptionItem[] items)
        {
            combo.Items.Clear();

            combo.DropDownStyle = ComboBoxStyle.DropDownList;

            combo.Items.AddRange(items);

            combo.SelectedIndex = 0;
        }


        // =====================================================
        // REFRESH COM PORTS
        // =====================================================

        private void RefreshComPorts()
        {
            string previous = cmbPort.Text;

            cmbPort.Items.Clear();

            string[] ports = SerialPort.GetPortNames();

            Array.Sort(ports, StringComparer.OrdinalIgnoreCase);

            cmbPort.Items.AddRange(ports);

            if (Array.IndexOf(ports, previous) >= 0)
            {
                cmbPort.SelectedItem = previous;
            }
            else if (ports.Length > 0)
            {
                cmbPort.SelectedIndex = 0;
            }
        }


        private void BtnRefresh_Click(object sender, EventArgs e)
        {
            RefreshComPorts();

            Log("Refresh COM ports.");
        }


        // =====================================================
        // CONNECT / DISCONNECT
        // =====================================================

        private async void BtnConnect_Click(object sender, EventArgs e)
        {
            try
            {
                if (serial.IsOpen)
                {
                    DisconnectSerial();

                    return;
                }


                if (string.IsNullOrWhiteSpace(cmbPort.Text))
                {
                    MessageBox.Show("Chưa chọn COM Port.");

                    return;
                }


                serial.PortName = cmbPort.Text;
                serial.BaudRate = BAUDRATE;
                serial.DataBits = 8;
                serial.Parity = Parity.None;
                serial.StopBits = StopBits.One;
                serial.Handshake = Handshake.None;
                serial.ReadTimeout = 500;
                serial.WriteTimeout = 1000;
                serial.DtrEnable = false;
                serial.RtsEnable = false;

                serial.ReadBufferSize = 4096;
                serial.WriteBufferSize = 1024;
                serial.ReceivedBytesThreshold = 1;


                serial.Open();

                serial.DiscardInBuffer();
                serial.DiscardOutBuffer();

                lock (rxLock)
                {
                    rxBuffer.Clear();
                }

                UpdateConnectionUI();

                Log("Connected: " + serial.PortName + " / 9600 8N1");


                // Chờ MCU / adapter ổn định rồi mới PING
                await Task.Delay(300);

                await TestPingAsync();
            }
            catch (Exception ex)
            {
                Log("Connection error: " + ex.Message);

                UpdateConnectionUI();
            }
        }


        private void DisconnectSerial()
        {
            lock (rxLock)
            {
                if (pendingReply != null)
                {
                    pendingReply.TrySetCanceled();

                    pendingReply = null;
                }

                rxBuffer.Clear();
            }


            try
            {
                if (serial.IsOpen)
                {
                    serial.Close();
                }
            }
            catch (Exception ex)
            {
                Log("Close error: " + ex.Message);
            }


            UpdateConnectionUI();

            Log("Disconnected");
        }


        // =====================================================
        // UPDATE CONNECTION UI
        // =====================================================

        private void UpdateConnectionUI()
        {
            bool connected = serial.IsOpen;

            btnConnect.Text = connected ? "Disconnect" : "Connect";

            cmbPort.Enabled = !connected;

            btnRefresh.Enabled = !connected;

            btnTestAT.Enabled = connected;

            btnGetConfig.Enabled = connected;

            btnApply.Enabled = connected;

            btnSave.Enabled = connected;

            btnGetInput.Enabled = connected;
        }


        // =====================================================
        // PING
        // =====================================================

        private async void BtnTestAT_Click(object sender, EventArgs e)
        {
            if (commandLock.CurrentCount == 0) return;

            await TestPingAsync();
        }


        private async Task TestPingAsync()
        {
            try
            {
                byte[] response =
                    await SendRequestAsync(
                        CMD_PING,
                        new byte[0],
                        TIMEOUT_MS,
                        READ_RETRIES
                    );

                /*
                 * Expected payload: 00 50 4F 4E 47
                 * 00 = STATUS OK, PONG = ASCII
                 */

                if (response.Length != 5)
                {
                    throw new Exception("Invalid PING response length.");
                }

                string message = Encoding.ASCII.GetString(response, 1, 4);

                if (message != "PONG")
                {
                    throw new Exception("Unexpected PING response: " + message);
                }

                Log("PING OK PONG");
            }
            catch (Exception ex)
            {
                Log("PING error: " + ex.Message);
            }
        }


        // =====================================================
        // GET CONFIG
        // =====================================================

        private async void BtnGetConfig_Click(object sender, EventArgs e)
        {
            if (commandLock.CurrentCount == 0) return;

            await GetConfigAsync();
        }


        private async Task GetConfigAsync()
        {
            try
            {
                byte[] p =
                    await SendRequestAsync(
                        CMD_GET_CFG,
                        new byte[0],
                        TIMEOUT_MS,
                        READ_RETRIES
                    );

                /*
                 * [0]    Status
                 * [1]    Node ID
                 * [2]    Reserved Destination
                 * [3:6]  Frequency LE32
                 * [7]    Bandwidth
                 * [8]    Spreading Factor
                 * [9]    Coding Rate
                 * [10]   TX Power
                 */

                if (p.Length != 11)
                {
                    throw new Exception("Invalid GET_CFG response.");
                }

                int nodeId = p[1];

                byte destination = p[2];

                uint frequency = ReadU32(p, 3);

                int bandwidth = p[7];

                int sf = p[8];

                int cr = p[9];

                int power = unchecked((sbyte)p[10]);


                if ((nodeId >= 1) && (nodeId <= 254))
                {
                    numNodeId.Value = nodeId;
                }

                SelectComboByValue(cmbFreq, (int)frequency);
                SelectComboByValue(cmbBw, bandwidth);
                SelectComboByValue(cmbSf, sf);
                SelectComboByValue(cmbCr, cr);

                if ((power >= numPower.Minimum) &&
                    (power <= numPower.Maximum))
                {
                    numPower.Value = power;
                }


                Log("========== CONFIG ==========");
                Log("NODE ID   = " + nodeId);
                Log("DEST      = 0x" + destination.ToString("X2"));
                Log("FREQUENCY = " + frequency + " Hz");
                Log("BANDWIDTH = " + bandwidth);
                Log("SF        = " + sf);
                Log("CR        = " + cr);
                Log("TX POWER  = " + power + " dBm");
                Log("============================");

                if (destination != BROADCAST_ID)
                {
                    Log("WARNING: STM32 is not using Broadcast configuration.");
                }
            }
            catch (Exception ex)
            {
                Log("GET CONFIG error: " + ex.Message);
            }
        }


        // =====================================================
        // APPLY CONFIG
        // =====================================================

        private async void BtnApply_Click(object sender, EventArgs e)
        {
            if (commandLock.CurrentCount == 0) return;

            await ApplyConfigAsync();
        }


        private async Task ApplyConfigAsync()
        {
            try
            {
                byte nodeId = (byte)numNodeId.Value;

                uint frequency = (uint)GetSelectedValue(cmbFreq);

                byte bandwidth = (byte)GetSelectedValue(cmbBw);

                byte sf = (byte)GetSelectedValue(cmbSf);

                byte cr = (byte)GetSelectedValue(cmbCr);

                sbyte power = (sbyte)numPower.Value;


                if (frequency != 433000000U)
                {
                    DialogResult result =
                        MessageBox.Show(
                            "Bạn đã kiểm tra RF matching " +
                            "và anten hỗ trợ tần số này chưa?",
                            "RF Frequency Warning",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Warning
                        );

                    if (result != DialogResult.Yes)
                    {
                        return;
                    }
                }


                /*
                 * [0]    Node ID
                 * [1]    Broadcast = FF
                 * [2:5]  Frequency LE32
                 * [6]    Bandwidth
                 * [7]    Spreading Factor
                 * [8]    Coding Rate
                 * [9]    TX Power
                 */

                byte[] payload = new byte[10];

                payload[0] = nodeId;

                payload[1] = BROADCAST_ID;

                WriteU32(payload, 2, frequency);

                payload[6] = bandwidth;

                payload[7] = sf;

                payload[8] = cr;

                payload[9] = unchecked((byte)power);


                // SET_CFG: không retry
                await SendRequestAsync(CMD_SET_CFG, payload, TIMEOUT_MS, 0);

                Log("APPLY OK: Configuration staged in RAM.");
                Log("Node ID = " + nodeId);
                Log("Transmission = BROADCAST (0xFF)");
                Log("Press SAVE FLASH to save configuration.");
            }
            catch (Exception ex)
            {
                Log("APPLY error: " + ex.Message);
            }
        }


        // =====================================================
        // SAVE FLASH + REBOOT
        // =====================================================

        private async void BtnSave_Click(object sender, EventArgs e)
        {
            if (commandLock.CurrentCount == 0) return;

            await SaveConfigAsync();
        }


        private async Task SaveConfigAsync()
        {
            try
            {
                DialogResult result =
                    MessageBox.Show(
                        "Lưu cấu hình vào Flash và khởi động lại STM32?",
                        "Save Configuration",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question
                    );

                if (result != DialogResult.Yes)
                {
                    return;
                }


                // SAVE: không retry (ghi Flash)
                await SendRequestAsync(CMD_SAVE, new byte[0], 6000, 0);

                Log("SAVE OK: Configuration stored in Flash.");


                try
                {
                    // REBOOT: không retry
                    await SendRequestAsync(CMD_REBOOT, new byte[0], TIMEOUT_MS, 0);

                    Log("REBOOT acknowledged.");
                }
                catch (TimeoutException)
                {
                    Log("Reboot response timeout. STM32 may already be restarting.");
                }

                Log("Wait for reboot, then press GET CONFIG.");
            }
            catch (Exception ex)
            {
                Log("SAVE error: " + ex.Message);
            }
        }


        // =====================================================
        // GET DIGITAL INPUT
        // =====================================================

        private async void BtnGetInput_Click(object sender, EventArgs e)
        {
            if (commandLock.CurrentCount == 0) return;

            await GetInputAsync();
        }


        private async Task GetInputAsync()
        {
            try
            {
                byte[] p =
                    await SendRequestAsync(
                        CMD_GET_INPUT,
                        new byte[0],
                        TIMEOUT_MS,
                        READ_RETRIES
                    );

                /*
                 * [0] Status
                 * [1] Input Mask
                 */

                if (p.Length != 2)
                {
                    throw new Exception("Invalid GET_INPUT response.");
                }

                byte mask = (byte)(p[1] & 0x0F);

                int in1 = (mask >> 0) & 1;
                int in2 = (mask >> 1) & 1;
                int in3 = (mask >> 2) & 1;
                int in4 = (mask >> 3) & 1;

                Log("========== INPUT ==========");
                Log("IN1 = " + in1);
                Log("IN2 = " + in2);
                Log("IN3 = " + in3);
                Log("IN4 = " + in4);
                Log("===========================");
            }
            catch (Exception ex)
            {
                Log("GET INPUT error: " + ex.Message);
            }
        }


        // =====================================================
        // SEND REQUEST (LOCK + RETRY)
        // =====================================================

        private async Task<byte[]> SendRequestAsync(
            byte command,
            byte[] payload,
            int timeoutMs = TIMEOUT_MS,
            int retries = 0)
        {
            // Chỉ cho phép một lệnh tại một thời điểm
            await commandLock.WaitAsync();

            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        return await SendOnceAsync(command, payload, timeoutMs);
                    }
                    catch (TimeoutException) when (attempt < retries)
                    {
                        Log("Timeout, retry " + (attempt + 1) + "/" + retries);

                        await Task.Delay(100);
                    }
                }
            }
            finally
            {
                commandLock.Release();
            }
        }


        // =====================================================
        // SEND ONE ATTEMPT
        // =====================================================

        private async Task<byte[]> SendOnceAsync(
            byte command,
            byte[] payload,
            int timeoutMs)
        {
            if (!serial.IsOpen)
            {
                throw new InvalidOperationException(
                    "COM Port chưa được kết nối."
                );
            }

            byte[] frame = BuildFrame(command, payload);

            var waiter =
                new TaskCompletionSource<byte[]>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );

            lock (rxLock)
            {
                serial.DiscardInBuffer();

                rxBuffer.Clear();

                expectedResponse = (byte)(command | 0x80);

                pendingReply = waiter;
            }

            try
            {
                Log("TX: " + ToHex(frame));

                // Ghi ở thread pool, không chặn UI
                await Task.Run(() => serial.Write(frame, 0, frame.Length));

                byte[] response;

                // Timeout hủy được, không để timer treo
                using (var cts = new CancellationTokenSource(timeoutMs))
                using (cts.Token.Register(() =>
                    waiter.TrySetException(
                        new TimeoutException(
                            "Module không phản hồi trong " + timeoutMs + " ms."
                        ))))
                {
                    response = await waiter.Task;
                }

                if (response.Length < 1)
                {
                    throw new Exception("Missing response status.");
                }

                if (response[0] != 0x00)
                {
                    throw new Exception(DecodeStatus(response[0]));
                }

                return response;
            }
            finally
            {
                lock (rxLock)
                {
                    if (ReferenceEquals(pendingReply, waiter))
                    {
                        pendingReply = null;
                    }
                }
            }
        }


        // =====================================================
        // BUILD BINARY CONFIG FRAME
        // =====================================================

        private static byte[] BuildFrame(byte command, byte[] payload)
        {
            if (payload == null)
            {
                payload = new byte[0];
            }

            if (payload.Length > 32)
            {
                throw new Exception("Configuration payload too large.");
            }

            /*
             * C3 3C A5 5A | CMD | LEN | PAYLOAD | CRC_L | CRC_H
             */

            byte[] frame = new byte[8 + payload.Length];

            Array.Copy(MAGIC, 0, frame, 0, MAGIC.Length);

            frame[4] = command;

            frame[5] = (byte)payload.Length;

            Array.Copy(payload, 0, frame, 6, payload.Length);

            // CRC16 over CMD + LEN + PAYLOAD
            ushort crc = CRC16(frame, 4, 2 + payload.Length);

            frame[6 + payload.Length] = (byte)crc;

            frame[7 + payload.Length] = (byte)(crc >> 8);

            return frame;
        }


        // =====================================================
        // CRC16 MODBUS
        // =====================================================

        private static ushort CRC16(byte[] data, int offset, int length)
        {
            ushort crc = 0xFFFF;

            for (int i = 0; i < length; i++)
            {
                crc ^= data[offset + i];

                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 0x0001) != 0)
                    {
                        crc = (ushort)((crc >> 1) ^ 0xA001);
                    }
                    else
                    {
                        crc >>= 1;
                    }
                }
            }

            return crc;
        }


        // =====================================================
        // SERIAL DATA RECEIVED
        // =====================================================

        private void Serial_DataReceived(
            object sender,
            SerialDataReceivedEventArgs e)
        {
            try
            {
                lock (rxLock)
                {
                    if (closing || !serial.IsOpen)
                    {
                        return;
                    }

                    int available = serial.BytesToRead;

                    if (available <= 0)
                    {
                        return;
                    }

                    byte[] data = new byte[available];

                    int received = serial.Read(data, 0, data.Length);

                    for (int i = 0; i < received; i++)
                    {
                        rxBuffer.Add(data[i]);
                    }

                    ProcessReceivedBufferLocked();

                    // Chặn rxBuffer phình khi có nhiễu liên tục
                    if (rxBuffer.Count > MAX_RX_BUFFER)
                    {
                        rxBuffer.RemoveRange(0, rxBuffer.Count - 64);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!closing)
                {
                    Log("RX error: " + ex.Message);
                }
            }
        }


        // =====================================================
        // PROCESS RECEIVED BUFFER (gọi khi đang giữ rxLock)
        // =====================================================

        private void ProcessReceivedBufferLocked()
        {
            while (rxBuffer.Count > 0)
            {
                int start = FindMagic(rxBuffer);

                // ---------------------------------------------
                // KHÔNG TÌM THẤY HEADER
                // ---------------------------------------------

                if (start < 0)
                {
                    int keep = 0;

                    int max = Math.Min(3, rxBuffer.Count);

                    // Giữ lại phần header bị cắt giữa chừng
                    for (int n = 1; n <= max; n++)
                    {
                        bool match = true;

                        for (int i = 0; i < n; i++)
                        {
                            if (rxBuffer[rxBuffer.Count - n + i] != MAGIC[i])
                            {
                                match = false;

                                break;
                            }
                        }

                        if (match)
                        {
                            keep = n;
                        }
                    }

                    int rawLength = rxBuffer.Count - keep;

                    if (rawLength > 0)
                    {
                        LogRaw(rxBuffer.GetRange(0, rawLength).ToArray());

                        rxBuffer.RemoveRange(0, rawLength);
                    }

                    break;
                }

                // ---------------------------------------------
                // BỎ DỮ LIỆU TRƯỚC HEADER
                // ---------------------------------------------

                if (start > 0)
                {
                    LogRaw(rxBuffer.GetRange(0, start).ToArray());

                    rxBuffer.RemoveRange(0, start);
                }

                // Frame tối thiểu 8 byte
                if (rxBuffer.Count < 8)
                {
                    break;
                }

                // ---------------------------------------------
                // KIỂM TRA ĐỘ DÀI
                // ---------------------------------------------

                int length = rxBuffer[5];

                if (length > 32)
                {
                    Log("Invalid configuration length.");

                    rxBuffer.RemoveAt(0);

                    continue;
                }

                int total = 8 + length;

                if (rxBuffer.Count < total)
                {
                    break;
                }

                // ---------------------------------------------
                // LẤY FRAME + KIỂM TRA CRC
                // ---------------------------------------------

                byte[] frame = rxBuffer.GetRange(0, total).ToArray();

                ushort receivedCRC =
                    (ushort)(frame[total - 2] | (frame[total - 1] << 8));

                ushort calculatedCRC = CRC16(frame, 4, 2 + length);

                if (receivedCRC != calculatedCRC)
                {
                    Log("RX CRC ERROR");

                    rxBuffer.RemoveAt(0);

                    continue;
                }

                // ---------------------------------------------
                // FRAME HỢP LỆ
                // ---------------------------------------------

                rxBuffer.RemoveRange(0, total);

                Log("RX: " + ToHex(frame));

                byte command = frame[4];

                byte[] response = new byte[length];

                Array.Copy(frame, 6, response, 0, length);

                if ((pendingReply != null) && (command == expectedResponse))
                {
                    pendingReply.TrySetResult(response);
                }
                else
                {
                    Log("Unsolicited response: CMD=0x" + command.ToString("X2"));
                }
            }
        }


        private void LogRaw(byte[] raw)
        {
            if (raw.Length <= MAX_RAW_LOG_BYTES)
            {
                Log("RX RAW: " + ToHex(raw));
            }
            else
            {
                byte[] head = new byte[MAX_RAW_LOG_BYTES];

                Array.Copy(raw, head, MAX_RAW_LOG_BYTES);

                Log(
                    "RX RAW (" + raw.Length + " bytes): " +
                    ToHex(head) + " ..."
                );
            }
        }


        // =====================================================
        // FIND MAGIC HEADER
        // =====================================================

        private static int FindMagic(List<byte> data)
        {
            for (int i = 0; i <= data.Count - MAGIC.Length; i++)
            {
                bool match = true;

                for (int j = 0; j < MAGIC.Length; j++)
                {
                    if (data[i + j] != MAGIC[j])
                    {
                        match = false;

                        break;
                    }
                }

                if (match)
                {
                    return i;
                }
            }

            return -1;
        }


        // =====================================================
        // COMBOBOX HELPERS
        // =====================================================

        private static int GetSelectedValue(ComboBox combo)
        {
            OptionItem item = combo.SelectedItem as OptionItem;

            if (item == null)
            {
                throw new Exception("Please select a configuration option.");
            }

            return item.Value;
        }


        private static void SelectComboByValue(ComboBox combo, int value)
        {
            for (int i = 0; i < combo.Items.Count; i++)
            {
                OptionItem item = combo.Items[i] as OptionItem;

                if ((item != null) && (item.Value == value))
                {
                    combo.SelectedIndex = i;

                    return;
                }
            }

            System.Diagnostics.Debug.WriteLine(
                "Unsupported configuration value: " +
                combo.Name + " = " + value
            );
        }


        // =====================================================
        // UINT32 LITTLE ENDIAN
        // =====================================================

        private static void WriteU32(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
            data[offset + 2] = (byte)(value >> 16);
            data[offset + 3] = (byte)(value >> 24);
        }


        private static uint ReadU32(byte[] data, int offset)
        {
            return
                (uint)data[offset] |
                ((uint)data[offset + 1] << 8) |
                ((uint)data[offset + 2] << 16) |
                ((uint)data[offset + 3] << 24);
        }


        // =====================================================
        // STATUS DECODER
        // =====================================================

        private static string DecodeStatus(byte status)
        {
            switch (status)
            {
                case 0x00:
                    return "OK";

                case 0x01:
                    return "Invalid configuration parameter.";

                case 0x02:
                    return "STM32 Flash operation failed.";

                case 0x03:
                    return "Unknown configuration command.";

                default:
                    return "STM32 status = 0x" + status.ToString("X2");
            }
        }


        // =====================================================
        // HEX FORMAT
        // =====================================================

        private static string ToHex(byte[] data)
        {
            return BitConverter.ToString(data).Replace("-", " ");
        }


        // =====================================================
        // LOG: chỉ enqueue, UI được cập nhật theo lô bởi timer
        // =====================================================

        private void Log(string message)
        {
            if (closing)
            {
                return;
            }

            logQueue.Enqueue(
                DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message
            );
        }


        private void LogTimer_Tick(object sender, EventArgs e)
        {
            if (logQueue.IsEmpty || txtLog == null || txtLog.IsDisposed)
            {
                return;
            }

            StringBuilder sb = new StringBuilder();

            string line;

            int n = 0;

            while (n < MAX_LOG_LINES_PER_TICK && logQueue.TryDequeue(out line))
            {
                sb.AppendLine(line);

                n++;
            }

            txtLog.AppendText(sb.ToString());

            // Giới hạn dung lượng log để AppendText không chậm dần
            if (txtLog.TextLength > MAX_LOG_CHARS)
            {
                string text = txtLog.Text;

                int cut = text.Length - (MAX_LOG_CHARS / 2);

                int nl = text.IndexOf('\n', cut);

                if (nl >= 0)
                {
                    cut = nl + 1;
                }

                txtLog.Text = text.Substring(cut);

                txtLog.SelectionStart = txtLog.TextLength;

                txtLog.ScrollToCaret();
            }
        }


        // =====================================================
        // FORM CLOSING
        // =====================================================

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            closing = true;

            logTimer.Stop();

            lock (rxLock)
            {
                if (pendingReply != null)
                {
                    pendingReply.TrySetCanceled();

                    pendingReply = null;
                }

                rxBuffer.Clear();
            }

            serial.DataReceived -= Serial_DataReceived;

            try
            {
                if (serial.IsOpen)
                {
                    serial.Close();
                }

                serial.Dispose();
            }
            catch (Exception)
            {
                // Ignore shutdown errors.
            }

            base.OnFormClosing(e);
        }


        // =====================================================
        // DESIGNER EVENT HANDLERS
        //
        // Cần giữ lại vì Form1.Designer.cs đang tham chiếu.
        // =====================================================

        private void Form1_Load(object sender, EventArgs e) { }

        private void groupBox1_Enter(object sender, EventArgs e) { }

        private void groupBox2_Enter(object sender, EventArgs e) { }

        private void groupBox3_Enter(object sender, EventArgs e) { }

        private void label1_Click(object sender, EventArgs e) { }

        private void label3_Click(object sender, EventArgs e) { }

        private void label4_Click(object sender, EventArgs e) { }

        private void label6_Click(object sender, EventArgs e) { }

        private void label7_Click(object sender, EventArgs e) { }

        private void label8_Click(object sender, EventArgs e) { }

        private void Bandwidth_Click(object sender, EventArgs e) { }

        private void numNodeId_ValueChanged(object sender, EventArgs e) { }

        private void numericUpDown1_ValueChanged(object sender, EventArgs e) { }

        private void numPower_ValueChanged(object sender, EventArgs e) { }

        private void cmbFreq_SelectedIndexChanged(object sender, EventArgs e) { }

        private void cmbBw_SelectedIndexChanged(object sender, EventArgs e) { }

        private void cmbSf_SelectedIndexChanged(object sender, EventArgs e) { }

        private void cmbCr_SelectedIndexChanged(object sender, EventArgs e) { }

        private void btnApply_Click_1(object sender, EventArgs e) { }

        private void btnSave_Click_1(object sender, EventArgs e) { }
    }


    // =====================================================
    // OPTION ITEM CLASS
    // =====================================================

    public class OptionItem
    {
        public string Text { get; set; }

        public int Value { get; set; }

        public OptionItem(string text, int value)
        {
            Text = text;

            Value = value;
        }

        public override string ToString()
        {
            return Text;
        }
    }
}