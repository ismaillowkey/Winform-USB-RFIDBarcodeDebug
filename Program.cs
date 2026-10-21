using System;
using System.Windows.Forms;

namespace WinformUSBRFID
{
    static class Program
    {
        /// <summary>
        /// Titik masuk utama untuk aplikasi.
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
