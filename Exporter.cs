using System;
using System.Collections.Generic;
using System.Data.Linq;
using System.Data.Linq.Mapping;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using HtmlAgilityPack;
using Microsoft.Data.Sqlite;

namespace WizNoteExporter;

class Exporter
{
    private readonly string _accountDirectory;
    private readonly string _outputDirectory;

    public Exporter(string accountDirectory, string outputDirectory)
    {
        _accountDirectory = accountDirectory;
        _outputDirectory = outputDirectory;
    }

    public static void ExportAll(string accountDirectory, string outputDirectory)
    {
        var stopWatch = Stopwatch.StartNew();

        using var sqlite = new SqliteConnection($@"Data Source={accountDirectory}\index.db");
        using var context = new DataContext(sqlite);

        var count = 0;

        var wizDocuments = new Dictionary<string, string>();

        foreach (var doc in context.GetTable<WizDocument>())
        {
            var ziwFilePath = accountDirectory + doc.Location + doc.FileName;

            wizDocuments.Add(doc.GUID, ziwFilePath);

            if (doc.Downloaded == 0)
            {
                Console.Error.WriteLine($"Need download: {doc.Title}");
                continue;
            }

            var modifiedTime = DateTime.ParseExact(
                doc.ModifiedTime, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var exporter = new Exporter(accountDirectory, outputDirectory);
            try
            {
                exporter.ExportZiw(ziwFilePath, doc.Title, modifiedTime);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"导出 {ziwFilePath} 出错：{ex.Message}");
            }

            count++;
        }

        // 检查附件是否已经下载
        foreach (var wizAttachment in context.GetTable<WizAttachment>())
        {
            wizDocuments.TryGetValue(wizAttachment.DocumentGUID, out var ziwFilePath);
            if (ziwFilePath == null)
            {
                Console.Error.WriteLine($"Cannot find Document for attachment \"{wizAttachment.FileName}\"");
                continue;
            }

            var attachmentFilePath =
                ziwFilePath.RemoveExtension(".ziw") + "_Attachments/" +
                ToValidFileName(wizAttachment.FileName)
                    .Replace('\'', '-')
                    .Replace(',', '-');
            if (!File.Exists(attachmentFilePath))
                Console.Error.WriteLine(
                    $"Cannot find attachment \"{wizAttachment.FileName}\" of document \"{ziwFilePath}\"");
        }

        Console.WriteLine($"{count} files processed in {stopWatch.Elapsed.TotalSeconds} seconds.");
    }

    [Table(Name = "WIZ_DOCUMENT")]
    private class WizDocument
    {
        [Column(Name = "DOCUMENT_GUID")]
        public string GUID { get; set; }

        [Column(Name = "DOCUMENT_TITLE")]
        public string Title { get; set; }

        [Column(Name = "DOCUMENT_LOCATION")]
        public string Location { get; set; }

        [Column(Name = "DOCUMENT_NAME")]
        public string FileName { get; set; }

        [Column(Name = "DT_DATA_MODIFIED")]
        public string ModifiedTime { get; set; }

        [Column(Name = "WIZ_DOWNLOADED")]
        public int Downloaded { get; set; }
    }

    [Table(Name = "WIZ_DOCUMENT_ATTACHMENT")]
    private class WizAttachment
    {
        [Column(Name = "DOCUMENT_GUID")]
        public string DocumentGUID { get; set; }

        [Column(Name = "ATTACHMENT_NAME")]
        public string FileName { get; set; }
    }

    private enum ExportFormat
    {
        Markdown,
        Text,
        Html,
        SourceCode,
    }

    private readonly string[] _sourceCodeExtensions =
    {
        ".pas", ".dpr",
        ".vbs", ".vb",
        ".bat", ".cmd", ".sh", ".ps1",
        ".sln",
        ".vcxproj", ".cpp", ".h", ".hpp",
        ".csproj", ".cs", ".go",
        ".py", ".lua", ".js", ".ts",
    };

    private HtmlDocument _htmlDoc = null!;
    private StringBuilder _output = null!;
    private string _outputFile = null!;
    private bool _forceText;
    private bool _hasImg;
    private string _titlePath = null!;
    private string _title = null!;
    private string _outputTitlePath = null!;
    private string _outputDir = null!;
    private ExportFormat _exportFormat;
    private string _ziwFile = null!;
    private ZipArchive _zip = null!;
    private DateTime _modifiedTime;

    public void ExportZiw(string ziwFile, string title, DateTime modifiedTime)
    {
        // if(!title.Contains("Knowledge for Programmer"))
        //     return;

        _ziwFile = ziwFile;
        _title = ToValidFileName(title);
        _modifiedTime = modifiedTime;

        // 加载index.html文档
        using (_zip = ZipFile.OpenRead(_ziwFile))
        {
            using var indexHtml = _zip.GetEntry("index.html")!.Open();
            _htmlDoc = new HtmlDocument();
            _htmlDoc.Load(indexHtml);

            // 计算路径
            _titlePath = Path.Combine(Path.GetDirectoryName(_ziwFile)!, _title);
            var relTitlePath = Path.GetRelativePath(_accountDirectory, _titlePath);
            _outputTitlePath = Path.Combine(_outputDirectory, relTitlePath);
            EnsureDirectory(_outputTitlePath);
            _outputDir = Path.GetDirectoryName(_outputTitlePath)!;

            // 拷贝附件目录
            if (CopyAttachments())
                return;

            // 导出笔记内容
            ExportDocument();

            // 解压index_files/
            ExtractIndexFiles();
        }
    }

    private static string ToValidFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var fileNameArray = fileName.ToCharArray();
        for (var i = 0; i < fileNameArray.Length; i++)
        {
            if (!invalidChars.Contains(fileNameArray[i]))
                continue;

            fileNameArray[i] = '-';
        }

        return new string(fileNameArray);
    }

    private bool CopyAttachments()
    {
        var attachmentsDir = _ziwFile.RemoveExtension(".ziw") + "_Attachments";
        if (!Directory.Exists(attachmentsDir))
            return false;

        var attachmentFiles = Directory.GetFiles(attachmentsDir);

        // 这个附件就是文档本身，直接把附件解压就好了
        if (attachmentFiles.Length == 1)
        {
            var attachmentFileName = Path.GetFileName(attachmentFiles[0]);
            if (attachmentFileName == _title || attachmentFileName == _title.Replace('_', ' '))
            {
                File.Copy(attachmentFiles[0], _outputTitlePath, true);
                return true;
            }
        }

        var outputAttachmentsDir = _outputTitlePath + "_Attachments";
        DirectoryCopy(attachmentsDir, outputAttachmentsDir, true);

        return false;
    }

    private readonly Encoding _utf8WithoutBom = new UTF8Encoding(false);

    private void ExportDocument()
    {
        _output = new StringBuilder();

        var titleExt = Path.GetExtension(_title).ToLower();
        if (titleExt == ".md")
        {
            _exportFormat = ExportFormat.Markdown;
            _outputFile = _outputTitlePath;
            _forceText = true;
            ExportMarkdown();
        }
        else if (titleExt == ".txt")
        {
            _exportFormat = ExportFormat.Text;
            _outputFile = _outputTitlePath;
            _forceText = true;
            ExportText();
            if (_hasImg)
            {
                _exportFormat = ExportFormat.Markdown;
                Path.ChangeExtension(_outputFile, ".md");
            }
        }
        else if (_sourceCodeExtensions.Contains(titleExt))
        {
            _exportFormat = ExportFormat.SourceCode;
            _outputFile = _outputTitlePath;
            _forceText = true;
            ExportText();
        }
        else
        {
            // 先测试是否txt，如果不是再输出html。
            _exportFormat = ExportFormat.Text;
            _outputFile = _outputTitlePath + ".txt";
            try
            {
                _forceText = false;
                ExportText();
                if (_hasImg)
                {
                    _exportFormat = ExportFormat.Markdown;
                    _outputFile = _outputTitlePath + ".md";
                }
            }
            catch
            {
                _exportFormat = ExportFormat.Html;
                _outputFile = _outputTitlePath + ".html";
                ExportHtml();
            }
        }

        // 写入最终笔记文件
        // 保持目标文件修改时间与源文件一致，方便多次导出，避免覆盖修改过的笔记
        var srcTime = _modifiedTime;
        if (!File.Exists(_outputFile) || srcTime >= File.GetLastWriteTime(_outputFile))
        {
            if (_exportFormat is ExportFormat.Html)
                _htmlDoc.Save(_outputFile, _utf8WithoutBom);
            else
                File.WriteAllText(_outputFile, _output.ToString(), _utf8WithoutBom);

            File.SetLastWriteTime(_outputFile, srcTime);
        }
        else
        {
            Console.WriteLine($"{_outputFile} has been modified, skip it.");
        }
    }

    private void ExtractIndexFiles()
    {
        var hasIndexFiles = false;
        var picDirPath =
            Path.Combine(_outputDir!, GetPictureDirName(_outputFile, _exportFormat == ExportFormat.Markdown));
        foreach (var entry in _zip.Entries)
        {
            if (entry.FullName is "index.html" or "wiz_mobile.html")
                continue;

            if (entry.FullName.StartsWith("index_files/"))
            {
                if (Path.GetFileName(entry.FullName).StartsWith("wizEditor"))
                    continue;

                var ext = Path.GetExtension(entry.FullName);
                if (ext == ".css" && _exportFormat is ExportFormat.Markdown or ExportFormat.Text)
                    continue;

                var entryOutputFile = Path.Combine(picDirPath, entry.FullName["index_files/".Length..]);
                EnsureDirectory(entryOutputFile);
                entry.ExtractToFile(entryOutputFile, true);

                hasIndexFiles = true;
            }
            else
            {
                throw new Exception($"Unexpected file in {_ziwFile}");
            }
        }

        if (_exportFormat is ExportFormat.Text && hasIndexFiles)
            Console.WriteLine($"Txt file {_ziwFile} has index_files.");
    }

    private void ExportText()
    {
        var body = _htmlDoc.DocumentNode.SelectSingleNode("//body");
        _hasImg = false;
        ProcessContent(body);
        EnsureEndOfFile(_output);
    }

    private void ExportMarkdown()
    {
        var body = _htmlDoc.DocumentNode.SelectSingleNode("//body");
        _hasImg = false;
        ProcessContent(body);
        EnsureEndOfFile(_output);
    }

    private void ProcessContent(HtmlNode contentNode)
    {
        foreach (var childNode in contentNode.ChildNodes)
            switch (childNode.Name)
            {
                case "pre":
                    // Lite markdown 只有一个 pre，包含了整个文档
                    if (_exportFormat == ExportFormat.Markdown)
                    {
                        _output.Clear();
                        _output.Append(DeEntitize(childNode.InnerText, true));
                        return;
                    }

                    _output.Append(DeEntitize(childNode.InnerText, true));

                    break;

                case "#text":
                    _output.Append(DeEntitize(childNode.InnerText));
                    break;

                case "br":
                    TrimAndAddLineEnding(_output);
                    break;

                case "img":
                    {
                        var src = childNode.Attributes["src"].Value;
                        if (src.StartsWith("index_files/"))
                        {
                            var imgFileName = src["index_files/".Length..];
                            var picDir = GetPictureDirName(_outputFile, true, true);
                            _output.Append($@"![{Path.ChangeExtension(imgFileName, null)}]({picDir}/{imgFileName})");
                        }
                        else
                        {
                            var checkedAttr = childNode.Attributes["data-wiz-check"]?.Value;
                            if (checkedAttr != null)
                                switch (checkedAttr)
                                {
                                    case "checked":
                                        _output.Append("- [x] ");
                                        break;
                                    case "unchecked":
                                        _output.Append("- [ ] ");
                                        break;
                                }
                            else
                                _output.Append($@"![{Path.GetFileNameWithoutExtension(src)}]({src})");
                        }

                        _hasImg = true;
                    }
                    break;

                case "span":
                case "a":
                case "font":
                    ProcessContent(childNode);
                    break;

                case "h1":
                case "h2":
                case "h3":
                case "h4":
                case "h5":
                case "h6":
                case "blockquote":
                case "label":
                case "b":
                case "strong":
                case "u":
                case "header":
                case "figure":
                case "small":
                case "code":
                    if (_forceText)
                        ProcessContent(childNode);
                    else
                        throw new Exception($"Unexpected tag \"{childNode.Name}\" for text file \"{_outputFile}\"");
                    break;

                case "div":
                case "p":
                    if (_output.Length > 0 && _output[^1] != '\n')
                        TrimAndAddLineEnding(_output);
                    ProcessContent(childNode);
                    if (_output.Length > 0 && _output[^1] != '\n')
                        TrimAndAddLineEnding(_output);
                    break;

                case "wiz_tmp_caret":
                case "#comment":
                case "style":
                case "meta":
                case "title":
                    continue;

                default:
                    throw new Exception($"Unexpected tag \"{childNode.Name}\" in \"{_outputFile}\"");
            }
    }

    private void ExportHtml()
    {
        // 设置标题
        var headNode = _htmlDoc.DocumentNode.SelectSingleNode("/html/head")
                       ?? _htmlDoc.DocumentNode.SelectSingleNode("/head");
        if (headNode == null)
        {
            Console.Error.WriteLine($"Cannot find <head> in {_ziwFile}");
        }
        else
        {
            var titleNode = headNode.SelectSingleNode("title");
            if (titleNode == null)
            {
                titleNode = _htmlDoc.CreateElement("title");
                headNode.AppendChild(titleNode);
            }

            titleNode.InnerHtml = _title;
        }

        // 修改本地图片的相对路径
        var imgNodes = _htmlDoc.DocumentNode.SelectNodes("//img");
        if (imgNodes != null)
            foreach (var imgNode in imgNodes)
            {
                var srcAttribute = imgNode.Attributes["src"];
                if (srcAttribute != null && srcAttribute.Value.StartsWith("index_files/"))
                    srcAttribute.Value =
                        $"{GetPictureDirName(_outputFile, false)}/{srcAttribute.Value["index_files/".Length..]}";
            }
    }

    private const string LineEnding = "\r\n";

    private static void TrimAndAddLineEnding(StringBuilder output)
    {
        // Trim all spaces
        var trimEndCount = 0;
        while (trimEndCount < output.Length
               && output[^(trimEndCount + 1)] is ' ')
            trimEndCount++;

        output.Remove(output.Length - trimEndCount, trimEndCount);

        // if output is empty, do nothing
        if (output.Length == 0)
            return;

        output.Append(LineEnding);
    }

    private static void EnsureEndOfFile(StringBuilder output)
    {
        // Trim all line ending and spaces
        var trimEndCount = 0;
        while (trimEndCount < output.Length
               && output[^(trimEndCount + 1)] is '\r' or '\n' or ' ')
            trimEndCount++;

        output.Remove(output.Length - trimEndCount, trimEndCount);

        // if output is empty, do nothing
        if (output.Length == 0)
            return;

        output.Append(LineEnding);
    }

    private static void DirectoryCopy(string sourceDirName, string destDirName, bool copySubDirs)
    {
        // Get the subdirectories for the specified directory.
        var dir = new DirectoryInfo(sourceDirName);

        if (!dir.Exists)
            throw new DirectoryNotFoundException(
                "Source directory does not exist or could not be found: "
                + sourceDirName);

        var dirs = dir.GetDirectories();

        // If the destination directory doesn't exist, create it.
        Directory.CreateDirectory(destDirName);

        // Get the files in the directory and copy them to the new location.
        var files = dir.GetFiles();
        foreach (var file in files)
        {
            var tempPath = Path.Combine(destDirName, file.Name);
            file.CopyTo(tempPath, true);
        }

        // If copying subdirectories, copy them and their contents to new location.
        if (copySubDirs)
            foreach (var subDir in dirs)
            {
                var tempPath = Path.Combine(destDirName, subDir.Name);
                DirectoryCopy(subDir.FullName, tempPath, copySubDirs);
            }
    }

    private static void EnsureDirectory(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (directory != null && !Directory.Exists(directory))
            Directory.CreateDirectory(directory);
    }

    private static string DeEntitize(string text, bool keepLineEnding = false)
    {
        var result = HtmlEntity.DeEntitize(text.Replace("&nbsp;", " "));
        if (!keepLineEnding)
            result = result.Replace(LineEnding, string.Empty);

        return result;
    }

    private static string GetPictureDirName(string docFile, bool isMarkdown, bool escapeSpace = false)
    {
        var titleNoExt = Path.GetFileName(docFile).RemoveExtension(".txt", ".md", ".html");
        var dirName = titleNoExt + (isMarkdown ? ".assets" : "_files");
        if (escapeSpace)
            dirName = dirName.Replace(" ", "%20");
        return dirName;
    }
}

static class StringPathExtensions
{
    public static string RemoveExtension(this string path, params string[] extensions)
    {
        return extensions.Contains(Path.GetExtension(path))
            ? Path.ChangeExtension(path, null)
            : path;
    }
}
