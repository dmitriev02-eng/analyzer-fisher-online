using System;
using System.IO;
using System.Text;

namespace FisherLogger
{
    /// <summary>
    /// Консольный режим для проверки импорта без GUI:
    /// FisherLogger.exe --pcap "D:\...\session_001.pcapng"
    ///
    /// Пишет два файла в D:\FisherLogger\logs:
    ///   import_&lt;имя&gt;.csv  — поимки с расширенными колонками (UTF-8 BOM);
    ///   import_&lt;имя&gt;.txt  — текстовый отчёт.
    /// Историю catch_data.json режим не изменяет.
    /// </summary>
    public static class HeadlessImport
    {
        public static int Run(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    WriteError("Файл не найден: " + path);
                    return 2;
                }

                var result = PcapImportRunner.Run(path);

                AppPaths.EnsureLogsDirectory();
                string baseName = Path.GetFileNameWithoutExtension(path);
                string csvPath = Path.Combine(AppPaths.LogsDirectory, "import_" + baseName + ".csv");
                string reportPath = Path.Combine(AppPaths.LogsDirectory, "import_" + baseName + ".txt");

                CatchCsvWriter.Write(csvPath, result.Catches);
                File.WriteAllText(reportPath, result.Report, new UTF8Encoding(false));

                Console.WriteLine(result.Report);
                Console.WriteLine("CSV: " + csvPath);
                Console.WriteLine("Отчёт: " + reportPath);
                return 0;
            }
            catch (Exception ex)
            {
                WriteError(ex.ToString());
                return 1;
            }
        }

        private static void WriteError(string text)
        {
            try
            {
                AppPaths.EnsureLogsDirectory();
                string path = Path.Combine(AppPaths.LogsDirectory, "import_error.txt");
                File.WriteAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine + text,
                    new UTF8Encoding(false));
                Console.WriteLine(text);
            }
            catch
            {
                // Последняя попытка — не мешать выходу.
            }
        }
    }
}
