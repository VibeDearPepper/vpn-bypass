using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace VpnBypassPackager
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Any(a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
                    return SelfTest();

                string root = AppDomain.CurrentDomain.BaseDirectory;
                string releases = Path.Combine(root, "releases");
                Directory.CreateDirectory(releases);
                string archive = Path.Combine(releases, "VpnBypass-" + DateTime.Now.ToString("yyyy.MM.dd-HHmmss") + ".zip");
                CreateArchive(root, archive);

                bool quiet = args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase));
                if (!quiet)
                    MessageBox.Show("Готовый архив создан:\n\n" + archive + "\n\nЛичные настройки и sites.json в архив не включены.",
                        "VPN Bypass — архив готов", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Не удалось создать архив", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        internal static void CreateArchive(string root, string archivePath)
        {
            string app = Path.Combine(root, "VpnBypass.exe");
            string readme = Path.Combine(root, "README.md");
            if (!File.Exists(app)) throw new FileNotFoundException("Не найден VpnBypass.exe.", app);
            if (!File.Exists(readme)) throw new FileNotFoundException("Не найден README.md.", readme);
            if (File.Exists(archivePath)) File.Delete(archivePath);

            using (FileStream stream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                AddFile(zip, app, "VpnBypass.exe");
                AddFile(zip, readme, "README.md");

                ZipArchiveEntry start = zip.CreateEntry("НАЧНИТЕ_ОТСЮДА.txt", CompressionLevel.Optimal);
                using (var writer = new StreamWriter(start.Open(), new UTF8Encoding(true)))
                {
                    writer.WriteLine("VPN Bypass");
                    writer.WriteLine();
                    writer.WriteLine("1. Распакуйте архив в отдельную папку.");
                    writer.WriteLine("2. Запустите VpnBypass.exe.");
                    writer.WriteLine("3. Подтвердите стандартный запрос UAC Windows.");
                    writer.WriteLine("4. Добавьте нужные сайты в интерфейсе приложения.");
                    writer.WriteLine();
                    writer.WriteLine("Программа создаёт только временные IPv4-маршруты.");
                }
            }
        }

        private static void AddFile(ZipArchive zip, string source, string name)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (Stream input = File.OpenRead(source))
            using (Stream output = entry.Open()) input.CopyTo(output);
        }

        private static int SelfTest()
        {
            string temp = Path.Combine(Path.GetTempPath(), "vpn-bypass-packager-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(temp);
                File.WriteAllText(Path.Combine(temp, "VpnBypass.exe"), "test", Encoding.UTF8);
                File.WriteAllText(Path.Combine(temp, "README.md"), "readme", Encoding.UTF8);
                File.WriteAllText(Path.Combine(temp, "sites.json"), "private", Encoding.UTF8);
                string zipPath = Path.Combine(temp, "release.zip");
                CreateArchive(temp, zipPath);
                using (ZipArchive zip = ZipFile.OpenRead(zipPath))
                {
                    string[] names = zip.Entries.Select(e => e.FullName).ToArray();
                    if (!names.Contains("VpnBypass.exe") || !names.Contains("README.md") || names.Contains("sites.json")) return 2;
                }
                Console.WriteLine("PACKAGER_SELF_TEST=OK");
                return 0;
            }
            finally
            {
                if (Directory.Exists(temp)) Directory.Delete(temp, true);
            }
        }
    }
}
