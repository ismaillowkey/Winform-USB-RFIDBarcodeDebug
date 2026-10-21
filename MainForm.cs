using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WinformUSBRFID
{
    public partial class MainForm : Form
    {
        #region Win32 Constants

        private const int WM_INPUT = 0x00FF;
        private const int WM_CHAR = 0x0102;
        private const uint RID_INPUT = 0x10000003;
        private const uint RIDI_DEVICENAME = 0x20000007;

        private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
        private const ushort HID_USAGE_GENERIC_KEYBOARD = 0x06;

        private const uint RIDEV_INPUTSINK = 0x00000100;
        private const uint RIDEV_REMOVE = 0x00000001;

        private const ushort RI_KEY_BREAK = 1;

        #endregion

        #region Win32 Structs

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICELIST
        {
            public IntPtr hDevice;
            public uint dwType;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWKEYBOARD
        {
            public ushort MakeCode;
            public ushort Flags;
            public ushort Reserved;
            public ushort VKey;
            public uint Message;
            public uint ExtraInformation;
        }

        #endregion

        #region Win32 P/Invoke

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetRawInputDeviceList(
            [In, Out] RAWINPUTDEVICELIST[] pRawInputDeviceList,
            ref uint puiNumDevices,
            uint cbSize);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        public static extern uint GetRawInputDeviceInfoA(
            IntPtr hDevice,
            uint uiCommand,
            StringBuilder pData,
            ref uint pcbSize);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterRawInputDevices(
            [In] RAWINPUTDEVICE[] pRawInputDevices,
            uint uiNumDevices,
            uint cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetRawInputData(
            IntPtr hRawInput,
            uint uiCommand,
            IntPtr pData,
            ref uint pcbSize,
            uint cbSizeHeader);

        [DllImport("user32.dll")]
        public static extern uint MapVirtualKey(uint uCode, uint uMapType);

        #endregion

        #region Device Model

        /// <summary>
        /// Model data untuk menampung item USB HID Device pada ComboBox.
        /// </summary>
        public class UsbHidDeviceItem
        {
            public IntPtr Handle { get; set; }
            public string DevicePath { get; set; }
            public string DisplayName { get; set; }
            public string VidPid { get; set; }
            public uint DeviceType { get; set; }

            public override string ToString()
            {
                return DisplayName;
            }
        }

        #endregion

        #region Fields

        private bool _isConnected = false;
        private UsbHidDeviceItem _connectedDevice = null;
        private readonly StringBuilder _scanBuffer = new StringBuilder();
        private readonly HashSet<string> _previousDevicePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _isInitialEnumeration = true;

        // Timer untuk auto-flush data jika terjadi jeda scan (safety timeout)
        private readonly Timer _inactivityFlushTimer = new Timer();
        private char _lastInputChar = '\0';
        private long _lastInputTick = 0;

        // Bahasa aktif aplikasi ("ID" untuk Bahasa Indonesia, "EN" untuk English)
        private string _currentLanguage = "ID";

        #endregion

        public MainForm()
        {
            InitializeComponent();

            // Tangkap input keyboard langsung di level Form
            this.KeyPreview = true;

            // Inisialisasi timer auto-flush (250ms setelah karakter terakhir diterima)
            _inactivityFlushTimer.Interval = 250;
            _inactivityFlushTimer.Tick += InactivityFlushTimer_Tick;

            // Inisialisasi ikon bendera Indonesia dan Inggris
            btnLangId.Image = CreateIndonesiaFlag();
            btnLangEn.Image = CreateEnglishFlag();

            // Terapkan bahasa default (Bahasa Indonesia)
            SetLanguage("ID");

            this.Load += MainForm_Load;
        }

        private void InactivityFlushTimer_Tick(object sender, EventArgs e)
        {
            _inactivityFlushTimer.Stop();
            FlushScanBuffer();
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            RefreshDeviceList();
        }

        #region Device Enumeration

        /// <summary>
        /// Memperbarui daftar perangkat USB HID pada ComboBox dan mendeteksi perangkat yang baru terhubung.
        /// </summary>
        private void RefreshDeviceList()
        {
            comboBoxDevices.Items.Clear();

            List<UsbHidDeviceItem> deviceList = EnumerateUsbHidDevices();

            UsbHidDeviceItem newlyConnectedItem = null;
            List<string> currentPaths = new List<string>();

            int calculatedWidth = comboBoxDevices.Width;
            using (Graphics g = comboBoxDevices.CreateGraphics())
            {
                for (int i = 0; i < deviceList.Count; i++)
                {
                    UsbHidDeviceItem item = deviceList[i];
                    currentPaths.Add(item.DevicePath);

                    // Periksa apakah perangkat ini baru terhubung setelah refresh
                    // (tidak ada dalam pemindaian sebelumnya dan bukan saat pertama kali form dimuat)
                    if (!_isInitialEnumeration)
                    {
                        if (!_previousDevicePaths.Contains(item.DevicePath))
                        {
                            string newPrefix = "[BARU] ";
                            string newLogMsg = "[INFO] Perangkat baru terhubung: ";

                            if (_currentLanguage == "EN")
                            {
                                newPrefix = "[NEW] ";
                                newLogMsg = "[INFO] New device connected: ";
                            }

                            // Berikan tanda pada nama tampilan ComboBox
                            item.DisplayName = newPrefix + item.DisplayName;

                            // Simpan referensi agar langsung terpilih otomatis di ComboBox
                            if (newlyConnectedItem == null)
                            {
                                newlyConnectedItem = item;
                            }

                            // Catat pemberitahuan ke RichTextBox bahwa perangkat baru terhubung
                            AppendScannedData(newLogMsg + item.DisplayName);
                        }
                    }

                    comboBoxDevices.Items.Add(item);

                    // Ukur lebar teks item untuk memastikan teks pada dropdown tidak terpotong
                    int textWidth = (int)g.MeasureString(item.DisplayName, comboBoxDevices.Font).Width + 35;
                    if (textWidth > calculatedWidth)
                    {
                        calculatedWidth = textWidth;
                    }
                }
            }

            // Perbarui daftar path perangkat sebelumnya
            _previousDevicePaths.Clear();
            for (int i = 0; i < currentPaths.Count; i++)
            {
                _previousDevicePaths.Add(currentPaths[i]);
            }

            _isInitialEnumeration = false;

            comboBoxDevices.DropDownWidth = calculatedWidth;

            // Pemilihan item ComboBox
            if (comboBoxDevices.Items.Count > 0)
            {
                // Jika ada perangkat yang baru terhubung, otomatis pilih perangkat tersebut
                if (newlyConnectedItem != null)
                {
                    comboBoxDevices.SelectedItem = newlyConnectedItem;
                }
                else
                {
                    comboBoxDevices.SelectedIndex = 0;
                }

                btnConnect.Enabled = true;
            }
            else
            {
                btnConnect.Enabled = false;
            }
        }

        /// <summary>
        /// Mengambil seluruh data perangkat USB HID yang terdeteksi pada sistem operasi Windows.
        /// </summary>
        private List<UsbHidDeviceItem> EnumerateUsbHidDevices()
        {
            List<UsbHidDeviceItem> result = new List<UsbHidDeviceItem>();
            uint numDevices = 0;
            uint structSize = (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST));

            // Ambil jumlah total perangkat
            GetRawInputDeviceList(null, ref numDevices, structSize);
            if (numDevices == 0)
            {
                return result;
            }

            RAWINPUTDEVICELIST[] devList = new RAWINPUTDEVICELIST[numDevices];
            GetRawInputDeviceList(devList, ref numDevices, structSize);

            for (int i = 0; i < numDevices; i++)
            {
                IntPtr hDev = devList[i].hDevice;
                uint charCount = 0;

                // Ambil ukuran string device path
                GetRawInputDeviceInfoA(hDev, RIDI_DEVICENAME, null, ref charCount);
                if (charCount > 0)
                {
                    StringBuilder sb = new StringBuilder((int)charCount + 1);
                    GetRawInputDeviceInfoA(hDev, RIDI_DEVICENAME, sb, ref charCount);
                    string rawPath = sb.ToString();

                    // Filter hanya perangkat bertipe USB HID (mengandung HID# atau USB#)
                    string upperPath = rawPath.ToUpper();
                    if (upperPath.Contains("HID#") || upperPath.Contains("USB#"))
                    {
                        string friendlyName = GetDeviceFriendlyName(rawPath);
                        string vidPid = ExtractVidPid(rawPath);

                        // Keterangan tipe antarmuka (Keyboard atau Generic HID)
                        string typeLabel = "";
                        if (devList[i].dwType == 1)
                        {
                            typeLabel = " [Keyboard]";
                        }
                        else if (devList[i].dwType == 2)
                        {
                            typeLabel = " [HID]";
                        }

                        // Susun nama tampilan untuk ComboBox
                        string displayName = "";
                        if (!string.IsNullOrEmpty(vidPid))
                        {
                            displayName = friendlyName + " (" + vidPid + ")" + typeLabel;
                        }
                        else
                        {
                            displayName = friendlyName + typeLabel;
                        }

                        UsbHidDeviceItem item = new UsbHidDeviceItem();
                        item.Handle = hDev;
                        item.DevicePath = rawPath;
                        item.DisplayName = displayName;
                        item.VidPid = vidPid;
                        item.DeviceType = devList[i].dwType;

                        result.Add(item);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Mengekstrak VID dan PID dari string device path.
        /// </summary>
        private static string ExtractVidPid(string path)
        {
            Match match = Regex.Match(path, @"VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                string vid = match.Groups[1].Value.ToUpper();
                string pid = match.Groups[2].Value.ToUpper();
                return "VID:" + vid + " PID:" + pid;
            }

            return "";
        }

        /// <summary>
        /// Membaca nama ramah (FriendlyName atau DeviceDesc) dari Windows Registry.
        /// </summary>
        private static string GetDeviceFriendlyName(string path)
        {
            try
            {
                string cleanPath = path;

                // Hapus prefix \\?\
                if (cleanPath.StartsWith(@"\\?\"))
                {
                    cleanPath = cleanPath.Substring(4);
                }

                // Pecah bagian path berdasarkan separator '#'
                string[] parts = cleanPath.Split('#');
                if (parts.Length >= 3)
                {
                    string subKeyPath = @"SYSTEM\CurrentControlSet\Enum\" + parts[0] + @"\" + parts[1] + @"\" + parts[2];
                    using (RegistryKey regKey = Registry.LocalMachine.OpenSubKey(subKeyPath))
                    {
                        if (regKey != null)
                        {
                            object friendlyVal = regKey.GetValue("FriendlyName");
                            if (friendlyVal != null)
                            {
                                string friendlyStr = friendlyVal.ToString().Trim();
                                if (!string.IsNullOrEmpty(friendlyStr))
                                {
                                    return friendlyStr;
                                }
                            }

                            object descVal = regKey.GetValue("DeviceDesc");
                            if (descVal != null)
                            {
                                string descStr = descVal.ToString().Trim();
                                int semicolonIdx = descStr.LastIndexOf(';');
                                if (semicolonIdx >= 0)
                                {
                                    if (semicolonIdx < descStr.Length - 1)
                                    {
                                        return descStr.Substring(semicolonIdx + 1).Trim();
                                    }
                                }
                                return descStr;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Bila terjadi kegagalan akses registry, gunakan fallback
            }

            return "USB HID Device";
        }

        /// <summary>
        /// Mengambil device path dari handle perangkat Raw Input.
        /// </summary>
        private static string GetDevicePathByHandle(IntPtr hDevice)
        {
            uint charCount = 0;
            GetRawInputDeviceInfoA(hDevice, RIDI_DEVICENAME, null, ref charCount);
            if (charCount > 0)
            {
                StringBuilder sb = new StringBuilder((int)charCount + 1);
                GetRawInputDeviceInfoA(hDevice, RIDI_DEVICENAME, sb, ref charCount);
                return sb.ToString();
            }

            return "";
        }

        #endregion

        #region Event Handlers

        private void btnRefresh_Click(object sender, EventArgs e)
        {
            RefreshDeviceList();
            richTextBoxData.Focus();
        }

        private void btnConnect_Click(object sender, EventArgs e)
        {
            if (comboBoxDevices.SelectedItem == null)
            {
                if (_currentLanguage == "ID")
                {
                    MessageBox.Show("Silakan pilih perangkat USB HID terlebih dahulu.", "Peringatan", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                else
                {
                    MessageBox.Show("Please select a USB HID device first.", "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }

            _connectedDevice = (UsbHidDeviceItem)comboBoxDevices.SelectedItem;
            _scanBuffer.Clear();
            _lastInputChar = '\0';
            _lastInputTick = 0;

            // Daftarkan Raw Input untuk Keyboard (UsagePage = 1, Usage = 6)
            // Menggunakan RIDEV_INPUTSINK agar tetap dapat menerima scan meski form kehilangan fokus
            RAWINPUTDEVICE[] rid = new RAWINPUTDEVICE[1];
            rid[0].usUsagePage = HID_USAGE_PAGE_GENERIC;
            rid[0].usUsage = HID_USAGE_GENERIC_KEYBOARD;
            rid[0].dwFlags = RIDEV_INPUTSINK;
            rid[0].hwndTarget = this.Handle;

            bool isSuccess = RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
            if (!isSuccess)
            {
                if (_currentLanguage == "ID")
                {
                    MessageBox.Show("Gagal menginisialisasi Raw Input listener.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    MessageBox.Show("Failed to initialize Raw Input listener.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return;
            }

            _isConnected = true;

            // Berikan status koneksi pada RichTextBox
            if (_currentLanguage == "ID")
            {
                AppendScannedData("[INFO] Terhubung ke " + _connectedDevice.DisplayName);
            }
            else
            {
                AppendScannedData("[INFO] Connected to " + _connectedDevice.DisplayName);
            }

            // Perbarui status kontrol UI
            btnConnect.Enabled = false;
            btnDisconnect.Enabled = true;
            btnRefresh.Enabled = false;
            comboBoxDevices.Enabled = false;

            // Arahkan fokus ke RichTextBox agar tombol tidak menahan fokus
            richTextBoxData.Focus();
        }

        private void btnDisconnect_Click(object sender, EventArgs e)
        {
            if (_isConnected)
            {
                // Hentikan timer dan kosongkan buffer
                _inactivityFlushTimer.Stop();
                _scanBuffer.Clear();

                // Lepaskan pendaftaran Raw Input listener
                RAWINPUTDEVICE[] rid = new RAWINPUTDEVICE[1];
                rid[0].usUsagePage = HID_USAGE_PAGE_GENERIC;
                rid[0].usUsage = HID_USAGE_GENERIC_KEYBOARD;
                rid[0].dwFlags = RIDEV_REMOVE;
                rid[0].hwndTarget = IntPtr.Zero;

                RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));

                _isConnected = false;
                _connectedDevice = null;

                if (_currentLanguage == "ID")
                {
                    AppendScannedData("[INFO] Terputus dari perangkat.");
                }
                else
                {
                    AppendScannedData("[INFO] Disconnected from device.");
                }
            }

            // Perbarui status kontrol UI
            btnConnect.Enabled = true;
            btnDisconnect.Enabled = false;
            btnRefresh.Enabled = true;
            comboBoxDevices.Enabled = true;

            // Arahkan fokus ke RichTextBox
            richTextBoxData.Focus();
        }

        private void btnClear_Click(object sender, EventArgs e)
        {
            richTextBoxData.Clear();

            // Arahkan fokus ke RichTextBox agar tombol Clear tidak menahan fokus
            richTextBoxData.Focus();
        }

        private void btnLangId_Click(object sender, EventArgs e)
        {
            SetLanguage("ID");
            richTextBoxData.Focus();
        }

        private void btnLangEn_Click(object sender, EventArgs e)
        {
            SetLanguage("EN");
            richTextBoxData.Focus();
        }

        /// <summary>
        /// Mengubah bahasa antarmuka aplikasi secara instan.
        /// </summary>
        private void SetLanguage(string lang)
        {
            _currentLanguage = lang;

            if (lang == "ID")
            {
                this.Text = "USB HID RFID & Barcode Reader";
                lblDevice.Text = "USB HID :";
                btnRefresh.Text = "Refresh";
                btnConnect.Text = "Connect";
                btnDisconnect.Text = "Disconnect";
                btnClear.Text = "Clear";
                lblNote.Text = "ℹ Catatan: Cara kerja software ini adalah menyadap (sniffing) data ketikan dari perangkat USB RFID / Barcode Scanner.";

                // Highlight bendera aktif (Indonesia)
                btnLangId.FlatAppearance.BorderColor = Color.FromArgb(0, 120, 215);
                btnLangId.FlatAppearance.BorderSize = 2;
                btnLangEn.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
                btnLangEn.FlatAppearance.BorderSize = 1;

                toolTip.SetToolTip(btnLangId, "Bahasa Indonesia (Aktif)");
                toolTip.SetToolTip(btnLangEn, "Ganti ke English");
            }
            else
            {
                this.Text = "USB HID RFID & Barcode Reader";
                lblDevice.Text = "USB HID :";
                btnRefresh.Text = "Refresh";
                btnConnect.Text = "Connect";
                btnDisconnect.Text = "Disconnect";
                btnClear.Text = "Clear";
                lblNote.Text = "ℹ Note: This software works by intercepting (sniffing) keystroke data from USB RFID / Barcode Scanner devices.";

                // Highlight bendera aktif (English)
                btnLangEn.FlatAppearance.BorderColor = Color.FromArgb(0, 120, 215);
                btnLangEn.FlatAppearance.BorderSize = 2;
                btnLangId.FlatAppearance.BorderColor = Color.FromArgb(200, 200, 200);
                btnLangId.FlatAppearance.BorderSize = 1;

                toolTip.SetToolTip(btnLangId, "Switch to Bahasa Indonesia");
                toolTip.SetToolTip(btnLangEn, "English (Active)");
            }
        }

        /// <summary>
        /// Membuat gambar bendera Indonesia (Merah Putih) menggunakan GDI+.
        /// </summary>
        private static Bitmap CreateIndonesiaFlag()
        {
            Bitmap bmp = new Bitmap(28, 18);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                using (SolidBrush redBrush = new SolidBrush(Color.FromArgb(218, 41, 28)))
                using (SolidBrush whiteBrush = new SolidBrush(Color.White))
                using (Pen borderPen = new Pen(Color.FromArgb(180, 180, 180)))
                {
                    // Bagian atas merah
                    g.FillRectangle(redBrush, 0, 0, 28, 9);

                    // Bagian bawah putih
                    g.FillRectangle(whiteBrush, 0, 9, 28, 9);

                    // Garis tepi halus
                    g.DrawRectangle(borderPen, 0, 0, 27, 17);
                }
            }
            return bmp;
        }

        /// <summary>
        /// Membuat gambar bendera Inggris (Union Jack) menggunakan GDI+.
        /// </summary>
        private static Bitmap CreateEnglishFlag()
        {
            Bitmap bmp = new Bitmap(28, 18);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                using (SolidBrush blueBrush = new SolidBrush(Color.FromArgb(1, 33, 105)))
                using (Pen whitePenDiag = new Pen(Color.White, 3))
                using (Pen redPenDiag = new Pen(Color.FromArgb(200, 16, 46), 1))
                using (Pen whitePenCross = new Pen(Color.White, 6))
                using (Pen redPenCross = new Pen(Color.FromArgb(200, 16, 46), 3))
                using (Pen borderPen = new Pen(Color.FromArgb(180, 180, 180)))
                {
                    // Latar biru
                    g.FillRectangle(blueBrush, 0, 0, 28, 18);

                    // Garis diagonal putih dan merah
                    g.DrawLine(whitePenDiag, 0, 0, 28, 18);
                    g.DrawLine(whitePenDiag, 0, 18, 28, 0);
                    g.DrawLine(redPenDiag, 0, 0, 28, 18);
                    g.DrawLine(redPenDiag, 0, 18, 28, 0);

                    // Palang tengah putih
                    g.DrawLine(whitePenCross, 14, 0, 14, 18);
                    g.DrawLine(whitePenCross, 0, 9, 28, 9);

                    // Palang tengah merah
                    g.DrawLine(redPenCross, 14, 0, 14, 18);
                    g.DrawLine(redPenCross, 0, 9, 28, 9);

                    // Garis tepi halus
                    g.DrawRectangle(borderPen, 0, 0, 27, 17);
                }
            }
            return bmp;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_isConnected)
            {
                _inactivityFlushTimer.Stop();

                RAWINPUTDEVICE[] rid = new RAWINPUTDEVICE[1];
                rid[0].usUsagePage = HID_USAGE_PAGE_GENERIC;
                rid[0].usUsage = HID_USAGE_GENERIC_KEYBOARD;
                rid[0].dwFlags = RIDEV_REMOVE;
                rid[0].hwndTarget = IntPtr.Zero;

                RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
                _isConnected = false;
            }

            base.OnFormClosing(e);
        }

        #endregion

        #region Windows Message Processing (Raw Input & Direct Keyboard)

        /// <summary>
        /// Mencegah penekanan tombol Enter/LF dari scanner RFID memicu klik otomatis pada tombol UI (seperti Clear atau Disconnect).
        /// </summary>
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_isConnected)
            {
                Keys keyCode = (keyData & Keys.KeyCode);

                // Jika scanner RFID mengirimkan Enter atau LineFeed di akhir scan
                if (keyCode == Keys.Enter || keyCode == Keys.Return || keyCode == Keys.LineFeed)
                {
                    _inactivityFlushTimer.Stop();
                    FlushScanBuffer();

                    // Kembalikan true agar tombol Enter dikonsumsi dan TIDAK memicu klik pada tombol yang sedang aktif
                    return true;
                }
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void WndProc(ref Message m)
        {
            // 1. Tangani pesan Raw Input (WM_INPUT)
            if (m.Msg == WM_INPUT)
            {
                ProcessRawInput(m.LParam);
            }
            // 2. Tangani pesan karakter langsung (WM_CHAR) saat form aktif, persis seperti Notepad
            else if (m.Msg == WM_CHAR)
            {
                if (_isConnected)
                {
                    char ch = (char)m.WParam.ToInt32();
                    ProcessDirectChar(ch);
                }
            }

            base.WndProc(ref m);
        }

        /// <summary>
        /// Memproses data WM_INPUT yang masuk dari keyboard/RFID reader.
        /// </summary>
        private void ProcessRawInput(IntPtr lParam)
        {
            if (!_isConnected)
            {
                return;
            }

            if (_connectedDevice == null)
            {
                return;
            }

            uint dwSize = 0;
            uint headerSize = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));

            // Ambil ukuran buffer yang diperlukan
            GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref dwSize, headerSize);
            if (dwSize == 0)
            {
                return;
            }

            IntPtr pData = Marshal.AllocHGlobal((int)dwSize);
            try
            {
                uint bytesCopied = GetRawInputData(lParam, RID_INPUT, pData, ref dwSize, headerSize);
                if (bytesCopied == 0 || bytesCopied == uint.MaxValue)
                {
                    return;
                }

                // Baca header data Raw Input
                RAWINPUTHEADER header = (RAWINPUTHEADER)Marshal.PtrToStructure(pData, typeof(RAWINPUTHEADER));

                // Pastikan input merupakan data Keyboard (dwType == 1)
                if (header.dwType != 1)
                {
                    return;
                }

                // Pastikan input berasal dari perangkat USB HID RFID yang sedang terkoneksi
                if (!IsInputFromConnectedDevice(header.hDevice))
                {
                    return;
                }

                // Hitung offset letak struktur RAWKEYBOARD (tepat setelah RAWINPUTHEADER)
                IntPtr pKeyboard = new IntPtr(pData.ToInt64() + Marshal.SizeOf(typeof(RAWINPUTHEADER)));
                RAWKEYBOARD keyboard = (RAWKEYBOARD)Marshal.PtrToStructure(pKeyboard, typeof(RAWKEYBOARD));

                // Hanya proses penekanan tombol ke bawah (Key Down / Make Code)
                bool isKeyDown = ((keyboard.Flags & RI_KEY_BREAK) == 0);
                if (isKeyDown)
                {
                    ushort vKey = keyboard.VKey;
                    ushort makeCode = keyboard.MakeCode;

                    // Beberapa perangkat RFID hanya mengirimkan MakeCode dan mengosongkan VKey (0 atau 0xFF)
                    if (vKey == 0 || vKey == 0xFF || vKey == 0xE7)
                    {
                        if (makeCode > 0)
                        {
                            uint mappedKey = MapVirtualKey((uint)makeCode, 1 /* MAPVK_VSC_TO_VK */);
                            if (mappedKey > 0)
                            {
                                vKey = (ushort)mappedKey;
                            }
                        }
                    }

                    HandleKeyDown(vKey, makeCode);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pData);
            }
        }

        /// <summary>
        /// Memverifikasi apakah handle perangkat pengirim input sesuai dengan perangkat yang dipilih.
        /// </summary>
        private bool IsInputFromConnectedDevice(IntPtr hDevice)
        {
            if (_connectedDevice == null)
            {
                return false;
            }

            // 1. Cocokkan langsung berdasarkan handle perangkat
            if (hDevice == _connectedDevice.Handle)
            {
                return true;
            }

            // 2. Jika hDevice bernilai IntPtr.Zero (kondisi umum driver generic keyboard wedge Windows)
            if (hDevice == IntPtr.Zero)
            {
                return true;
            }

            // 3. Cocokkan berdasarkan path perangkat
            string incomingPath = GetDevicePathByHandle(hDevice);
            if (!string.IsNullOrEmpty(incomingPath))
            {
                // Cocokkan path lengkap
                if (string.Equals(incomingPath, _connectedDevice.DevicePath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                // Cocokkan berdasarkan VID dan PID
                if (!string.IsNullOrEmpty(_connectedDevice.VidPid))
                {
                    string incomingVidPid = ExtractVidPid(incomingPath);
                    if (!string.IsNullOrEmpty(incomingVidPid))
                    {
                        if (string.Equals(incomingVidPid, _connectedDevice.VidPid, StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    // Periksa apakah incomingPath memuat substring VID dan PID perangkat
                    if (incomingPath.IndexOf(_connectedDevice.VidPid, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Menangani karakter dari penekanan tombol dan mendeteksi batas akhir data (LF / Enter).
        /// </summary>
        private void HandleKeyDown(ushort vKey, ushort makeCode)
        {
            // Deteksi batas akhir data RFID: LF (0x0A), Enter (0x0D), atau MakeCode Enter (0x1C)
            if (vKey == 0x0D || vKey == 0x0A || makeCode == 0x1C)
            {
                _inactivityFlushTimer.Stop();
                FlushScanBuffer();
                return;
            }

            // Konversi Virtual Key ke representasi karakter teks
            char mappedChar = ConvertVirtualKeyToChar(vKey);

            // Jika belum terpetakan dan MakeCode tersedia, coba petakan via scan code
            if (mappedChar == '\0' && makeCode > 0)
            {
                uint vkFromMake = MapVirtualKey((uint)makeCode, 1 /* MAPVK_VSC_TO_VK */);
                if (vkFromMake > 0)
                {
                    mappedChar = ConvertVirtualKeyToChar((ushort)vkFromMake);
                }
            }

            // Periksa jika mappedChar menghasilkan newline '\n' atau carriage return '\r'
            if (mappedChar == '\n' || mappedChar == '\r')
            {
                _inactivityFlushTimer.Stop();
                FlushScanBuffer();
                return;
            }

            // Jika karakter valid dan dapat dicetak, masukkan ke dalam buffer
            if (mappedChar != '\0')
            {
                _scanBuffer.Append(mappedChar);
                _lastInputChar = mappedChar;
                _lastInputTick = Environment.TickCount;

                // Reset timer timeout jika pembacaan belum selesai
                _inactivityFlushTimer.Stop();
                _inactivityFlushTimer.Start();
            }
        }

        /// <summary>
        /// Memproses karakter yang diterima langsung melalui pesan WM_CHAR (sinkron seperti Notepad).
        /// </summary>
        private void ProcessDirectChar(char ch)
        {
            // Jika karakter adalah Enter / LF
            if (ch == '\r' || ch == '\n')
            {
                _inactivityFlushTimer.Stop();
                FlushScanBuffer();
                return;
            }

            // Abaikan tombol kontrol lainnya (Escape, Backspace, Tab, dll)
            if (char.IsControl(ch))
            {
                return;
            }

            // Cek duplikasi jika karakter yang sama baru saja dimasukkan via Raw Input dalam rentang < 20ms
            long currentTick = Environment.TickCount;
            if (ch == _lastInputChar)
            {
                long elapsed = currentTick - _lastInputTick;
                if (elapsed >= 0 && elapsed < 20)
                {
                    return;
                }
            }

            _lastInputChar = ch;
            _lastInputTick = currentTick;

            _scanBuffer.Append(ch);

            // Reset timer auto-flush
            _inactivityFlushTimer.Stop();
            _inactivityFlushTimer.Start();
        }

        /// <summary>
        /// Melakukan flush data dari buffer scan ke RichTextBox.
        /// </summary>
        private void FlushScanBuffer()
        {
            if (_scanBuffer.Length > 0)
            {
                string scannedText = _scanBuffer.ToString().Trim();
                _scanBuffer.Clear();

                if (!string.IsNullOrEmpty(scannedText))
                {
                    AppendScannedData(scannedText);
                }
            }
        }

        /// <summary>
        /// Mengonversi Virtual Key Code menjadi karakter ASCII / teks yang sesuai.
        /// </summary>
        private static char ConvertVirtualKeyToChar(ushort vKey)
        {
            // Baris angka utama: '0' s/d '9' (0x30 s/d 0x39)
            if (vKey >= 0x30 && vKey <= 0x39)
            {
                int diff = vKey - 0x30;
                return (char)('0' + diff);
            }

            // Tombol angka pada Numpad: '0' s/d '9' (0x60 s/d 0x69)
            if (vKey >= 0x60 && vKey <= 0x69)
            {
                int diff = vKey - 0x60;
                return (char)('0' + diff);
            }

            // Huruf 'A' s/d 'Z' (0x41 s/d 0x5A)
            if (vKey >= 0x41 && vKey <= 0x5A)
            {
                int diff = vKey - 0x41;
                return (char)('A' + diff);
            }

            // Pemetaan umum karakter lainnya menggunakan MapVirtualKey
            uint charVal = MapVirtualKey((uint)vKey, 2 /* MAPVK_VK_TO_CHAR */);
            if (charVal > 0)
            {
                char c = (char)charVal;

                // Abaikan tombol kontrol sistem (seperti Escape, Tab, Backspace, dll)
                if (!char.IsControl(c) || c == '\n' || c == '\r')
                {
                    return c;
                }
            }

            return '\0';
        }

        /// <summary>
        /// Menambahkan data hasil scan RFID ke dalam RichTextBox secara thread-safe.
        /// </summary>
        private void AppendScannedData(string data)
        {
            if (this.InvokeRequired)
            {
                this.BeginInvoke(new Action<string>(AppendScannedData), data);
                return;
            }

            // Tambahkan baris data hasil scan
            richTextBoxData.AppendText(data + Environment.NewLine);

            // Geser scrollbar otomatis ke bagian paling bawah
            richTextBoxData.SelectionStart = richTextBoxData.TextLength;
            richTextBoxData.ScrollToCaret();
        }

        #endregion
    }
}
