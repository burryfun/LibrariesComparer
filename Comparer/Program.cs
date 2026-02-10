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
    public List<VulnerabilityInfo> Vulnerabilities { get; set; } = new();
    public DeprecationInfo Deprecation { get; set; }
}

public class VulnerabilityInfo
{
    public string AdvisoryUrl { get; set; }
    public string Severity { get; set; }
}

public class DeprecationInfo
{
    public string Message { get; set; }
    public List<string> Reasons { get; set; } = new();
    public string AlternatePackageId { get; set; }
}

class Program
{
    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    });

    static Program()
    {
        HttpClient.DefaultRequestHeaders.Add("User-Agent", "LibraryComparerBot/1.0");
    }

    static async Task Main(string[] args)
    {
        try
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
            var libraries = await GetLibrariesInfoAsync(allPackages);

            // 3. Сравнение с прошлым снэпшотом
            var snapshotDir = Path.Combine(Directory.GetCurrentDirectory(), "snapshots");
            Directory.CreateDirectory(snapshotDir);
            List<LibraryInfo> prevLibraries = LoadPreviousLibraries(snapshotDir);
            libraries = HandleLibrariesByPrevious(libraries, prevLibraries);

            // 4. Сохраняем текущий снэпшот
            string filePath = SaveSnapshot(libraries, snapshotDir);
            if (!string.IsNullOrEmpty(filePath))
            {
                SaveExcelFromSnapshot(filePath);
                SaveHtmlTableFromSnapshot(filePath);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Критическая ошибка: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }

    private static string SaveSnapshot(List<LibraryInfo> libraries, string snapshotDir)
    {
        try
        {
            string fileName = $"{DateTime.Now:dd_MM_yyyy_HH-mm}.json";
            string filePath = Path.Combine(snapshotDir, fileName);
            var json = JsonSerializer.Serialize(libraries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
            Console.WriteLine($"json файл {filePath} создан.");
            return filePath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при сохранении снэпшота: {ex.Message}");
            return null;
        }
    }

    private static List<LibraryInfo> HandleLibrariesByPrevious(List<LibraryInfo> libraries, List<LibraryInfo> prevLibraries)
    {
        try
        {
            var prevDict = prevLibraries?
                .GroupBy(x => x.Name + "|" + x.Project)
                .ToDictionary(g => g.Key, g => g.First()) 
                ?? new Dictionary<string, LibraryInfo>();

            var newDict = libraries
                .GroupBy(x => x.Name + "|" + x.Project)
                .ToDictionary(g => g.Key, g => g.First());

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
                            Color = LibraryColor.Red,
                            Vulnerabilities = prev.Vulnerabilities,
                            Deprecation = prev.Deprecation
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
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при сравнении со снэпшотом: {ex.Message}");
        }

        return libraries;
    }

    private static List<LibraryInfo> LoadPreviousLibraries(string snapshotDir)
    {
        var prevSnapshot = Directory.GetFiles(snapshotDir, "*.json")
                    .OrderByDescending(f => f)
                    .FirstOrDefault();
        List<LibraryInfo> prevLibraries = null;
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

    private static async Task<List<LibraryInfo>> GetLibrariesInfoAsync(List<(string Project, string Name, string Version, string ReleaseDate, string NugetUrl, bool IsDll)> allPackages)
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
                Console.WriteLine($"[{current}/{total}] Пакет: {pkg.Name} {pkg.Version ?? ""} (проект: {pkg.Project})");
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
                            Version = info.resolvedVersion,
                            ReleaseDate = info.releaseDate,
                            Project = pkg.Project,
                            NugetUrl = info.url,
                            Vulnerabilities = info.vulnerabilities,
                            Deprecation = info.deprecation
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при обработке пакета {pkg.Name} {pkg.Version}: {ex.Message}");
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
            try
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
                                releaseDate = FormatDate(File.GetLastWriteTime(dllPath).ToString());
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Ошибка при получении версии DLL {dllPath}: {ex.Message}");
                            }
                        }
                    }
                    allPackages.Add((projectName, reference.Name, version, releaseDate, nugetUrl, true));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при обработке файла {csproj}: {ex.Message}");
            }
        }
        return allPackages;
    }

    private static async Task<IEnumerable<NuGetVersion>> GetPackageVersionsAsync(string packageName)
    {
        try
        {
            var url = $"https://api.nuget.org/v3-flatcontainer/{packageName.ToLowerInvariant()}/index.json";
            var response = await HttpClient.GetStreamAsync(url);
            using var doc = await JsonDocument.ParseAsync(response);
            return doc.RootElement.GetProperty("versions")
                .EnumerateArray()
                .Select(v => NuGetVersion.Parse(v.GetString()!))
                .ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при получении версий пакета {packageName}: {ex.Message}");
            return Enumerable.Empty<NuGetVersion>();
        }
    }

    static async Task<(string releaseDate, string url, List<VulnerabilityInfo> vulnerabilities, DeprecationInfo deprecation, string resolvedVersion)> GetNugetInfo(string packageName, string version)
    {
        var lowerName = packageName.ToLowerInvariant();
        string resolvedVersion = version;
        
        if (version.Contains('*') && VersionRange.TryParse(version, out var range))
        {
            var allVersions = await GetPackageVersionsAsync(packageName);
            var bestVersion = range.FindBestMatch(allVersions);
            if (bestVersion != null)
            {
                resolvedVersion = bestVersion.ToNormalizedString();
            }
        }

        if (!NuGetVersion.TryParse(resolvedVersion, out var targetVersion))
        {
            return ("", $"https://www.nuget.org/packages/{packageName}/{resolvedVersion}", new List<VulnerabilityInfo>(), null, resolvedVersion);
        }
        var normalizedVersion = targetVersion.ToNormalizedString().ToLowerInvariant();
        
        string semver1Url = $"https://api.nuget.org/v3/registration5-semver1/{lowerName}/{normalizedVersion}.json";
        var semver2Url = $"https://api.nuget.org/v3/registration5-gz-semver2/{lowerName}/index.json";
        string nugetUrl = $"https://www.nuget.org/packages/{packageName}/{resolvedVersion}";
        
        List<VulnerabilityInfo> vulnerabilities = new();
        DeprecationInfo deprecation = null;
        string published = "";

        try
        {
            var response = await HttpClient.GetStringAsync(semver1Url);
            using var doc = JsonDocument.Parse(response);
            published = doc.RootElement.TryGetProperty("published", out var p) ? p.GetString() ?? "" : "";

            if (doc.RootElement.TryGetProperty("vulnerabilities", out var vProp))
            {
                vulnerabilities = ParseVulnerabilities(vProp);
            }

            if (doc.RootElement.TryGetProperty("deprecation", out var dProp))
            {
                deprecation = ParseDeprecation(dProp);
            }

        if (deprecation == null)
        {
            if (doc.RootElement.TryGetProperty("catalogEntry", out var catalogProp))
            {
                var extra = await GetInfoFromCatalogProperty(catalogProp);
                if (vulnerabilities.Count == 0) vulnerabilities = extra.vulnerabilities;
                if (deprecation == null) deprecation = extra.deprecation;
            }
        }

        if (!string.IsNullOrEmpty(published))
            return (FormatDate(published), nugetUrl, vulnerabilities, deprecation, resolvedVersion);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Не удалось получить информацию о пакете {packageName} по адресу {semver1Url}: {ex.Message}");
        }

        try
        {
            Console.WriteLine($"Пробуем по: {semver2Url}");
            var indexJson = await HttpClient.GetStreamAsync(semver2Url);
            using var indexDoc = await JsonDocument.ParseAsync(indexJson);
            foreach (var page in indexDoc.RootElement.GetProperty("items").EnumerateArray())
            {
                string lower = page.GetProperty("lower").GetString()!;
                string upper = page.GetProperty("upper").GetString()!;

                if (!NuGetVersion.TryParse(lower, out var lowerVer) || !NuGetVersion.TryParse(upper, out var upperVer) || targetVersion < lowerVer || targetVersion > upperVer)
                {
                    continue;
                }

                JsonElement? foundEntry = null;
                if (page.TryGetProperty("items", out var entries))
                {
                    foreach (var e in entries.EnumerateArray())
                    {
                        var entryVerStr = e.GetProperty("catalogEntry").GetProperty("version").GetString()!;
                        if (NuGetVersion.TryParse(entryVerStr, out var entryVer) && entryVer.Equals(targetVersion))
                        {
                            foundEntry = e;
                            break;
                        }
                    }
                }
                else if (page.TryGetProperty("@id", out var pageUrlProp))
                {
                    var pageResponse = await HttpClient.GetStringAsync(pageUrlProp.GetString());
                    using var pageDoc = JsonDocument.Parse(pageResponse);
                    foreach (var e in pageDoc.RootElement.GetProperty("items").EnumerateArray())
                    {
                        var entryVerStr = e.GetProperty("catalogEntry").GetProperty("version").GetString()!;
                        if (NuGetVersion.TryParse(entryVerStr, out var entryVer) && entryVer.Equals(targetVersion))
                        {
                            foundEntry = e.Clone();
                            break;
                        }
                    }
                }

                if (foundEntry.HasValue)
                {
                    var entry = foundEntry.Value;
                    published = entry.TryGetProperty("published", out var p) ? p.GetString() ?? "" : "";
                    
                    if (entry.TryGetProperty("vulnerabilities", out var vProp))
                    {
                        vulnerabilities = ParseVulnerabilities(vProp);
                    }
                    if (entry.TryGetProperty("deprecation", out var dProp))
                    {
                        deprecation = ParseDeprecation(dProp);
                    }

                    var catalogProp = entry.GetProperty("catalogEntry");
                    published = string.IsNullOrEmpty(published) && catalogProp.TryGetProperty("published", out var cp) ? cp.GetString() ?? "" : published;
                    
                    if (vulnerabilities.Count == 0 && catalogProp.TryGetProperty("vulnerabilities", out var cvProp))
                        vulnerabilities = ParseVulnerabilities(cvProp);
                    if (deprecation == null && catalogProp.TryGetProperty("deprecation", out var cdProp))
                        deprecation = ParseDeprecation(cdProp);

                    // Если всё еще нет данных, пробуем по ссылке в catalogEntry
                    if (vulnerabilities.Count == 0 || deprecation == null)
                    {
                        var extra = await GetInfoFromCatalogProperty(catalogProp);
                        if (vulnerabilities.Count == 0) vulnerabilities = extra.vulnerabilities;
                        if (deprecation == null) deprecation = extra.deprecation;
                    }

                    return (FormatDate(published), nugetUrl, vulnerabilities, deprecation, resolvedVersion);
                }
            }
        }
        catch (Exception) { }

        return (FormatDate(published), nugetUrl, vulnerabilities, deprecation, resolvedVersion);
    }

    private static string FormatDate(string dateStr)
    {
        if (DateTime.TryParse(dateStr, out var dt))
        {
            return dt.ToString("dd-MM-yyyy");
        }
        return dateStr;
    }

    private static async Task<(List<VulnerabilityInfo> vulnerabilities, DeprecationInfo deprecation)> GetInfoFromCatalogProperty(JsonElement catalogProp)
    {
        string catalogUrl = null;
        if (catalogProp.ValueKind == JsonValueKind.String)
        {
            catalogUrl = catalogProp.GetString();
        }
        else if (catalogProp.ValueKind == JsonValueKind.Object)
        {
            if (catalogProp.TryGetProperty("@id", out var idProp))
            {
                catalogUrl = idProp.GetString();
            }
        }

        if (!string.IsNullOrEmpty(catalogUrl))
        {
            return await GetExtraInfoFromCatalog(catalogUrl);
        }
        return (new List<VulnerabilityInfo>(), null);
    }

    private static List<VulnerabilityInfo> ParseVulnerabilities(JsonElement element)
    {
        var list = new List<VulnerabilityInfo>();
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in element.EnumerateArray())
            {
                list.Add(new VulnerabilityInfo
                {
                    AdvisoryUrl = v.GetProperty("advisoryUrl").GetString() ?? "",
                    Severity = v.GetProperty("severity").GetString() ?? ""
                });
            }
        }
        return list;
    }

    private static DeprecationInfo ParseDeprecation(JsonElement element)
    {
        var info = new DeprecationInfo
        {
            Message = element.TryGetProperty("message", out var m) ? m.GetString() : ""
        };
        if (element.TryGetProperty("reasons", out var r) && r.ValueKind == JsonValueKind.Array)
        {
            foreach (var reason in r.EnumerateArray())
            {
                info.Reasons.Add(reason.GetString() ?? "");
            }
        }
        if (element.TryGetProperty("alternatePackage", out var alt))
        {
            if (alt.ValueKind == JsonValueKind.Object && alt.TryGetProperty("id", out var altId))
            {
                info.AlternatePackageId = altId.GetString();
            }
        }
        return info;
    }

    private static async Task<(List<VulnerabilityInfo> vulnerabilities, DeprecationInfo deprecation)> GetExtraInfoFromCatalog(string catalogUrl)
    {
        try
        {
            var response = await HttpClient.GetStringAsync(catalogUrl);
            using var doc = JsonDocument.Parse(response);
            List<VulnerabilityInfo> vulnerabilities = new();
            DeprecationInfo deprecation = null;

            if (doc.RootElement.TryGetProperty("vulnerabilities", out var vProp))
            {
                vulnerabilities = ParseVulnerabilities(vProp);
            }
            if (doc.RootElement.TryGetProperty("deprecation", out var dProp))
            {
                deprecation = ParseDeprecation(dProp);
            }
            return (vulnerabilities, deprecation);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] Error fetching catalog info: {ex.Message}");
        }
        return (new List<VulnerabilityInfo>(), null);
    }

    static void SaveExcelFromSnapshot(string jsonPath)
    {
        try
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
                    Vulnerabilities = FormatVulnerabilities(g.First().Vulnerabilities),
                    Deprecation = FormatDeprecation(g.First().Deprecation),
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
            ws.Cells[1, 5].Value = "Уязвимости";
            ws.Cells[1, 6].Value = "Депрекация";
            ws.Cells[1, 7].Value = "Ссылка";

            // Данные
            int row = 2;
            foreach (var lib in grouped)
            {
                ws.Cells[row, 1].Value = lib.Name;
                ws.Cells[row, 2].Value = lib.Version;
                ws.Cells[row, 3].Value = lib.ReleaseDate;
                ws.Cells[row, 4].Value = lib.Projects;
                ws.Cells[row, 5].Value = lib.Vulnerabilities;
                ws.Cells[row, 6].Value = lib.Deprecation;
                ws.Cells[row, 7].Value = lib.NugetUrl;

                var color = ColorTranslator.FromHtml(lib.Color.ToString());
                using (var range = ws.Cells[row, 1, row, 7])
                {
                    range.Style.Fill.PatternType = ExcelFillStyle.Solid;
                    range.Style.Fill.BackgroundColor.SetColor(color);
                }

                row++;
            }

            int lastRow = row - 1;
            int lastCol = 7;

            using (var tableRange = ws.Cells[1, 1, lastRow, lastCol])
            {
                tableRange.Style.Border.Top.Style = ExcelBorderStyle.Thin;
                tableRange.Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
                tableRange.Style.Border.Left.Style = ExcelBorderStyle.Thin;
                tableRange.Style.Border.Right.Style = ExcelBorderStyle.Thin;
            }

            using (var headerRange = ws.Cells[1, 1, 1, 7])
            {
                headerRange.Style.Font.Bold = true;
            }

            ws.Cells[ws.Dimension.Address].AutoFitColumns();

            package.SaveAs(new FileInfo(xlsxPath));
            Console.WriteLine($"Excel файл создан: {xlsxPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при создании Excel файла: {ex.Message}");
        }
    }

    private static string FormatVulnerabilities(List<VulnerabilityInfo> vulnerabilities)
    {
        if (vulnerabilities == null || vulnerabilities.Count == 0) return "";
        return string.Join("; ", vulnerabilities.Select(v => $"{GetSeverityName(v.Severity)} ({v.AdvisoryUrl})"));
    }

    private static string GetSeverityName(string severity) => severity switch
    {
        "0" => "Low",
        "1" => "Moderate",
        "2" => "High",
        "3" => "Critical",
        _ => severity
    };

    private static string FormatDeprecation(DeprecationInfo deprecation)
    {
        if (deprecation == null) return "";
        var reasonsList = (deprecation.Reasons != null && deprecation.Reasons.Count > 0) ? deprecation.Reasons : new List<string>();
        var reasons = string.Join(", ", reasonsList);
        var alt = !string.IsNullOrEmpty(deprecation.AlternatePackageId) ? $" (Alt: {deprecation.AlternatePackageId})" : "";
        var msg = !string.IsNullOrEmpty(deprecation.Message) ? $" {deprecation.Message}" : "";
        var result = $"{reasons}{alt}{msg}".Trim();
        
        return string.IsNullOrEmpty(result) ? "Deprecated" : result;
    }

    static void SaveHtmlTableFromSnapshot(string jsonPath)
    {
        try
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
                    Vulnerabilities = FormatVulnerabilities(g.First().Vulnerabilities),
                    Deprecation = FormatDeprecation(g.First().Deprecation),
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
            sb.AppendLine("<tr><th>Название библиотеки</th><th>Версия</th><th>Дата выпуска</th><th>Проекты</th><th>Уязвимости</th><th>Депрекация</th><th>Ссылка</th></tr>");
            foreach (var lib in grouped)
            {
                sb.AppendLine($"<tr class='{lib.Color}'><td>{WebUtility.HtmlEncode(lib.Name)}</td><td>{WebUtility.HtmlEncode(lib.Version)}</td><td>{WebUtility.HtmlEncode(lib.ReleaseDate)}</td><td>{WebUtility.HtmlEncode(lib.Projects)}</td><td>{WebUtility.HtmlEncode(lib.Vulnerabilities)}</td><td>{WebUtility.HtmlEncode(lib.Deprecation)}</td><td><a href='{lib.NugetUrl}'>{lib.NugetUrl}</a></td></tr>");
            }
            sb.AppendLine("</table></body></html>");
            File.WriteAllText(htmlPath, sb.ToString(), System.Text.Encoding.UTF8);
            Console.WriteLine($"HTML-таблица создана: {htmlPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при создании HTML-таблицы: {ex.Message}");
        }
    }
}
