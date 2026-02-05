using NuGet.Versioning;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using System.Drawing;
using System.Net;
using System.Text.Json;
using System.Xml.Linq;

namespace Comparer;

public enum LibraryColor
{
    White,
    Green,
    Yellow,
    Red
}

public class LibraryInfo
{
    public string Name { get; set; }
    public string Version { get; set; }
    public string ReleaseDate { get; set; }
    public string Project { get; set; }
    public string NugetUrl { get; set; }
    public LibraryColor Color { get; set; }
}


class Program
{
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    });

    static async Task Main(string[] args)
    {
        if (args.Length == 0 || !Directory.Exists(args[0]))
        {
            Console.WriteLine("Укажите путь к папке с проектами.");
            return;
        }

        string rootPath = args[0];

        var csprojFiles = Directory.GetFiles(rootPath, "*.csproj", SearchOption.AllDirectories);
        Console.WriteLine($"Найдено проектов: {csprojFiles.Length}");

        // 1. Собираем все пакеты из всех проектов
        var allPackages = GetAllPackages(csprojFiles);

        // 2. Получаем информацию из NuGet
        var libraries = await GetLibrariesInfoAsyncn(allPackages);

        // 3. Сравнение с прошлым снэпшотом
        var snapshotDir = Path.Combine(Directory.GetCurrentDirectory(), "snapshots");
        Directory.CreateDirectory(snapshotDir);
        List<LibraryInfo>? prevLibraries = LoadPreviousLibraries(snapshotDir);
        libraries = HandleLibrariesByPrevious(libraries, prevLibraries);

        // 4. Сохраняем текущий снэпшот
        string filePath = SaveSnapshot(libraries, snapshotDir);
        SaveExcelFromSnapshot(filePath);
        SaveHtmlTableFromSnapshot(filePath);
    }

    private static string SaveSnapshot(List<LibraryInfo> libraries, string snapshotDir)
    {
        string fileName = $"{DateTime.Now:dd_MM_yyyy_HH-mm}.json";
        string filePath = Path.Combine(snapshotDir, fileName);
        var json = JsonSerializer.Serialize(libraries, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(filePath, json);
        Console.WriteLine($"json файл {filePath} создан.");
        return filePath;
    }

    private static List<LibraryInfo> HandleLibrariesByPrevious(List<LibraryInfo> libraries, List<LibraryInfo>? prevLibraries)
    {
        var prevDict = prevLibraries?.ToDictionary(x => x.Name + "|" + x.Project) ?? new Dictionary<string, LibraryInfo>();
        var newDict = libraries.ToDictionary(x => x.Name + "|" + x.Project);

        // 1. Новые библиотеки (green), изменённые версии (yellow), те же (white)
        foreach (var lib in libraries)
        {
            var key = lib.Name + "|" + lib.Project;

            if (!prevDict.ContainsKey(key))
            {
                lib.Color = LibraryColor.Green;
            }
            else if (prevDict[key].Version != lib.Version)
            {   
                lib.Color = LibraryColor.Yellow;
            }
            else
            {
                lib.Color = LibraryColor.White;
            }
        }
        // 2. Удалённые библиотеки (red)
        if (prevLibraries != null)
        {
            foreach (var prev in prevLibraries)
            {
                var key = prev.Name + "|" + prev.Project;

                if (!newDict.ContainsKey(key) && prev.Color != LibraryColor.Red)
                {
                    libraries.Add(new LibraryInfo
                    {
                        Name = prev.Name,
                        Version = prev.Version,
                        ReleaseDate = prev.ReleaseDate,
                        Project = prev.Project,
                        NugetUrl = prev.NugetUrl,
                        Color = LibraryColor.Red
                    });
                }
            }
        }
        // 3. Исключаем библиотеки, которые были удалены в прошлом снэпшоте
        if (prevLibraries != null)
        {
            libraries = libraries.Where(lib =>
                !(prevDict.TryGetValue(lib.Name + "|" + lib.Project, out var prev) && prev.Color == LibraryColor.Red && !newDict.ContainsKey(lib.Name + "|" + lib.Project))
            ).ToList();
        }

        return libraries;
    }

    private static List<LibraryInfo>? LoadPreviousLibraries(string snapshotDir)
    {
        var prevSnapshot = Directory.GetFiles(snapshotDir, "*.json")
                    .OrderByDescending(f => f)
                    .FirstOrDefault();
        List<LibraryInfo>? prevLibraries = null;
        if (prevSnapshot != null)
        {
            try
            {
                var prevJson = File.ReadAllText(prevSnapshot);
                prevLibraries = JsonSerializer.Deserialize<List<LibraryInfo>>(prevJson);
            }
            catch { }
        }

        return prevLibraries;
    }

    private static async Task<List<LibraryInfo>> GetLibrariesInfoAsyncn(List<(string Project, string Name, string Version, string ReleaseDate, string NugetUrl, bool IsDll)> allPackages)
    {
        int total = allPackages.Count;
        int processed = 0;
        var libraries = new List<LibraryInfo>();

        const int maxConcurrency = 16;
        var semaphore = new SemaphoreSlim(maxConcurrency);
        var tasks = allPackages.Select(async pkg =>
        {
            await semaphore.WaitAsync();
            try
            {
                int current = Interlocked.Increment(ref processed);
                Console.WriteLine($"[{current}/{total}] Пакет: {pkg.Name} {(pkg.Version ?? "")} (проект: {pkg.Project})");
                if (pkg.IsDll)
                {
                    // DLL: не делаем запрос к NuGet
                    lock (libraries)
                    {
                        libraries.Add(new LibraryInfo
                        {
                            Name = pkg.Name,
                            Version = pkg.Version,
                            ReleaseDate = pkg.ReleaseDate,
                            Project = pkg.Project,
                            NugetUrl = pkg.NugetUrl
                        });
                    }
                }
                else
                {
                    var info = await GetNugetInfo(pkg.Name, pkg.Version);
                    lock (libraries)
                    {
                        libraries.Add(new LibraryInfo
                        {
                            Name = pkg.Name,
                            Version = pkg.Version,
                            ReleaseDate = info.releaseDate,
                            Project = pkg.Project,
                            NugetUrl = info.url
                        });
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks);
        return libraries;
    }

    private static List<(string Project, string Name, string Version, string ReleaseDate, string NugetUrl, bool IsDll)> GetAllPackages(string[] csprojFiles)
    {
        var allPackages = new List<(string Project, string Name, string Version, string ReleaseDate, string NugetUrl, bool IsDll)>();
        foreach (var csproj in csprojFiles)
        {
            var projectName = Path.GetFileNameWithoutExtension(csproj);
            var doc = XDocument.Load(csproj);
            // NuGet пакеты
            var packageRefs = doc.Descendants()
                .Where(x => x.Name.LocalName == "PackageReference")
                .Select(x => new
                {
                    Name = x.Attribute("Include")?.Value,
                    Version = x.Attribute("Version")?.Value
                })
                .Where(x => !string.IsNullOrEmpty(x.Name) && !string.IsNullOrEmpty(x.Version))
                .ToList();
            foreach (var pkg in packageRefs)
            {
                allPackages.Add((projectName, pkg.Name, pkg.Version, null, null, false));
            }
            // DLL библиотеки
            var references = doc.Descendants()
                .Where(x => x.Name.LocalName == "Reference")
                .Select(x => new
                {
                    Name = x.Attribute("Include")?.Value,
                    HintPath = x.Elements().FirstOrDefault(e => e.Name.LocalName == "HintPath")?.Value
                })
                .Where(x => !string.IsNullOrEmpty(x.Name))
                .ToList();
            foreach (var reference in references)
            {
                string version = null;
                string releaseDate = null;
                string nugetUrl = "(local dll)";
                if (!string.IsNullOrEmpty(reference.HintPath))
                {
                    string dllPath = Path.IsPathRooted(reference.HintPath) ? reference.HintPath : Path.Combine(Path.GetDirectoryName(csproj), reference.HintPath);
                    if (File.Exists(dllPath))
                    {
                        try
                        {
                            var fileVersionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(dllPath);
                            version = fileVersionInfo.FileVersion;
                            releaseDate = File.GetLastWriteTime(dllPath).ToString("dd-MM-yyyy");
                        }
                        catch { }
                    }
                }
                allPackages.Add((projectName, reference.Name, version, releaseDate, nugetUrl, true));
            }
        }
        return allPackages;
    }

    static async Task<(string releaseDate, string url)> GetNugetInfo(string packageName, string version)
    {
        var lowerName = packageName.ToLower();
        string semver1Url = $"https://api.nuget.org/v3/registration5-semver1/{lowerName}/{version}.json";
        var semver2Url = $"https://api.nuget.org/v3/registration5-gz-semver2/{lowerName}/index.json";
        string nugetUrl = $"https://www.nuget.org/packages/{packageName}/{version}";

        try
        {
            var response = await HttpClient.GetStringAsync(semver1Url);
            using var doc = JsonDocument.Parse(response);
            var published = doc.RootElement.GetProperty("published").GetString();
            return (published, nugetUrl);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Пакет не найден по адресу: {semver1Url}");
        }

        try
        {
            Console.WriteLine($"Пробуем по: {semver2Url}");
            var indexJson = await HttpClient.GetStringAsync(semver2Url);
            using var indexDoc = JsonDocument.Parse(indexJson);
            foreach (var page in indexDoc.RootElement.GetProperty("items").EnumerateArray())
            {
                string lower = page.GetProperty("lower").GetString()!;
                string upper = page.GetProperty("upper").GetString()!;

                var targetVersion = NuGetVersion.Parse(version);

                if (targetVersion < NuGetVersion.Parse(lower) || targetVersion > NuGetVersion.Parse(upper))
                {
                    continue;
                }

                if (page.TryGetProperty("items", out var entries))
                {
                    foreach (var entry in entries.EnumerateArray())
                    {
                        var catalog = entry.GetProperty("catalogEntry");
                        if (NuGetVersion.Parse(catalog.GetProperty("version").GetString()!).Equals(targetVersion))
                            return (catalog.GetProperty("published").GetString() ?? "", nugetUrl);
                    }
                }
                else if (page.TryGetProperty("@id", out var pageUrlProp))
                {
                    var pageJson = await HttpClient.GetStringAsync(pageUrlProp.GetString());
                    using var pageDoc = JsonDocument.Parse(pageJson);
                    foreach (var entry in pageDoc.RootElement.GetProperty("items").EnumerateArray())
                    {
                        var catalog = entry.GetProperty("catalogEntry");
                        if (NuGetVersion.Parse(catalog.GetProperty("version").GetString()!).Equals(targetVersion))
                            return (catalog.GetProperty("published").GetString() ?? "", nugetUrl);
                    }
                }
            }
        }
        catch(Exception ex)
        {
            Console.WriteLine($"Пакет не найден по адресу: {semver2Url}");
        }

        return ("", $"https://www.nuget.org/packages/{packageName}");
    }

    static void SaveExcelFromSnapshot(string jsonPath)
    {
        var json = File.ReadAllText(jsonPath);
        var libraries = JsonSerializer.Deserialize<List<LibraryInfo>>(json);

        var grouped = libraries
            .GroupBy(l => new { l.Name, l.Version, l.ReleaseDate, l.NugetUrl, l.Color })
            .Select(g => new
            {
                g.Key.Name,
                g.Key.Version,
                g.Key.ReleaseDate,
                g.Key.NugetUrl,
                g.Key.Color,
                Projects = string.Join(", ", g.Select(x => x.Project).Distinct().OrderBy(p => p))
            })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var xlsxPath = Path.ChangeExtension(jsonPath, ".xlsx");

        ExcelPackage.License.SetNonCommercialPersonal("My Name");

        using var package = new ExcelPackage();
        var ws = package.Workbook.Worksheets.Add("Libraries");

        // Заголовки
        ws.Cells[1, 1].Value = "Название библиотеки";
        ws.Cells[1, 2].Value = "Версия";
        ws.Cells[1, 3].Value = "Дата выпуска";
        ws.Cells[1, 4].Value = "Проекты";
        ws.Cells[1, 5].Value = "Ссылка";

        // Данные
        int row = 2;
        foreach (var lib in grouped)
        {
            ws.Cells[row, 1].Value = lib.Name;
            ws.Cells[row, 2].Value = lib.Version;
            ws.Cells[row, 3].Value = lib.ReleaseDate;
            ws.Cells[row, 4].Value = lib.Projects;
            ws.Cells[row, 5].Value = lib.NugetUrl;

            var color = ColorTranslator.FromHtml(lib.Color.ToString());
            using (var range = ws.Cells[row, 1, row, 5])
            {
                range.Style.Fill.PatternType = ExcelFillStyle.Solid;
                range.Style.Fill.BackgroundColor.SetColor(color);
            }

            row++;
        }

        int lastRow = row - 1;
        int lastCol = 5;

        using (var tableRange = ws.Cells[1, 1, lastRow, lastCol])
        {
            tableRange.Style.Border.Top.Style = ExcelBorderStyle.Thin;
            tableRange.Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
            tableRange.Style.Border.Left.Style = ExcelBorderStyle.Thin;
            tableRange.Style.Border.Right.Style = ExcelBorderStyle.Thin;
        }

        using (var headerRange = ws.Cells[1, 1, 1, 5])
        {
            headerRange.Style.Font.Bold = true;
        }

        ws.Cells[ws.Dimension.Address].AutoFitColumns();

        package.SaveAs(new FileInfo(xlsxPath));
        Console.WriteLine($"Excel файл создан: {xlsxPath}");
    }

    static void SaveHtmlTableFromSnapshot(string jsonPath)
    {
        var json = File.ReadAllText(jsonPath);
        var libraries = JsonSerializer.Deserialize<List<LibraryInfo>>(json);
        var grouped = libraries
            .GroupBy(l => new { l.Name, l.Version, l.ReleaseDate, l.NugetUrl, l.Color })
            .Select(g => new
            {
                g.Key.Name,
                g.Key.Version,
                g.Key.ReleaseDate,
                g.Key.NugetUrl,
                Color = g.Any(x => x.Color == LibraryColor.Green) ? "green" :
                        g.Any(x => x.Color == LibraryColor.Yellow) ? "yellow" :
                        g.Any(x => x.Color == LibraryColor.Red) ? "red" : "white",
                Projects = string.Join(", ", g.Select(x => x.Project).Distinct()),
            })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var htmlPath = Path.ChangeExtension(jsonPath, ".html");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<html><head><meta charset='utf-8'><style>table{border-collapse:collapse;}td,th{border:1px solid #ccc;padding:4px;}tr.green{background:#c6efce;}tr.yellow{background:#fff2cc;}tr.red{background:#ffc7ce;}tr.white{background:#fff;}</style></head><body>");
        sb.AppendLine("<table>");
        sb.AppendLine("<tr><th>Название библиотеки</th><th>Версия</th><th>Дата выпуска</th><th>Проекты</th><th>Ссылка</th></tr>");
        foreach (var lib in grouped)
        {
            sb.AppendLine($"<tr class='{lib.Color}'><td>{System.Net.WebUtility.HtmlEncode(lib.Name)}</td><td>{System.Net.WebUtility.HtmlEncode(lib.Version)}</td><td>{System.Net.WebUtility.HtmlEncode(lib.ReleaseDate)}</td><td>{System.Net.WebUtility.HtmlEncode(lib.Projects)}</td><td><a href='{lib.NugetUrl}'>{lib.NugetUrl}</a></td></tr>");
        }
        sb.AppendLine("</table></body></html>");
        File.WriteAllText(htmlPath, sb.ToString(), System.Text.Encoding.UTF8);
        Console.WriteLine($"HTML-таблица создана: {htmlPath}");
    }
}
