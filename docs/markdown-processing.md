# Markdown 处理说明

本文档描述 `WizNoteExporter` 在导出为知笔记 `.ziw` 时，与 Markdown 相关的处理逻辑，对应实现见 [Exporter.cs](../Exporter.cs)。

## 整体流程

每个 `.ziw` 文件实质上是一个 ZIP 包，内部包含 `index.html`、`index_files/`（图片等资源）以及可能的附件目录。导出流程在 `Exporter.ExportZiw` 中：

1. 打开 `.ziw`，加载 `index.html` 为 `HtmlDocument`。
2. 计算输出路径，处理同名附件目录（`CopyAttachments`）。
3. 选择导出格式并写出文件（`ExportDocument`）。
4. 解压 `index_files/` 下的资源到对应目录（`ExtractIndexFiles`）。
5. 写出时把目标文件的修改时间设为 `index.db` 中记录的笔记修改时间，便于重复导出时跳过手工修改过的文件。

## 导出格式的判定

`ExportDocument` 根据笔记标题的扩展名决定 `ExportFormat`：

| 标题扩展名 | 导出格式 | 说明 |
| --- | --- | --- |
| `.md` | `Markdown` | 直接走 `ExportMarkdown`。 |
| `.txt` | `Text` | 走 `ExportText`；若过程中遇到 `<img>`，事后改写为 `.md`。 |
| 源码后缀（`.cs` / `.py` / `.cpp` …见 `_sourceCodeExtensions`） | `SourceCode` | 与文本一样按纯文本导出，保持原扩展名。 |
| 其它 | 先尝试 `Text` | 失败则回退到 `Html`；若是文本但含图，升级为 `Markdown`。 |

也就是说，**进入 Markdown 分支有两条路径**：

- 标题就是 `*.md`：直接当 Markdown 导出。
- 标题是 `*.txt` 或无明确扩展名，但正文 `ProcessContent` 中遇到 `<img>`：把扩展名改为 `.md`，按 Markdown 处理。

## Markdown 的核心写出：`ExportMarkdown`

```text
ExportMarkdown
  ├─ ProcessContent(body)         // HTML → Markdown 文本
  ├─ NormalizeMarkdownHeadings()  // 规整标题
  └─ EnsureEndOfFile()            // 收尾换行
```

### `ProcessContent`：HTML 节点 → Markdown 文本

`ProcessContent` 是一个针对子节点 `Name` 的 `switch`，逐层递归把 HTML 拍平到 `_output` (`StringBuilder`) 上。Markdown 模式下相关分支如下：

- **`<pre>`**：
  - Lite Markdown 笔记的整篇内容都装在单个 `<pre>` 里。Markdown 模式遇到 `<pre>` 时**清空已收集的输出**，写入 `<pre>` 的 `InnerText`（保留换行），然后直接 `return`，整篇笔记到此为止。
  - 这意味着以 `<pre>` 整篇承载内容的笔记会绕开后续 HTML 节点遍历，原文按原样落盘。
- **`#text`**：写入文本，去掉行尾换行（由 `DeEntitize(text)` 完成）。
- **`<br>`**：把行尾空格 trim 掉，并追加一个换行（`TrimAndAddLineEnding`）。
- **`<img>`**：
  - `src` 以 `index_files/` 开头：转成本地图片的 Markdown 引用 `![名称](图片目录/文件)`。图片目录名由 `GetPictureDirName` 计算，Markdown 模式下使用 `<标题>.assets`，并对空格做 `%20` 转义。
  - 否则若有 `data-wiz-check` 属性（为知的待办块），转换成 GitHub 风格的复选框：`- [x] ` / `- [ ] `。
  - 其它情况按原 `src` 写出 `![名称](src)`。
  - 任意 `<img>` 出现都会把 `_hasImg` 置为 `true`，这是文本→Markdown 升级的依据。
- **`<span>` / `<a>` / `<font>`**：忽略自身标签，递归处理子节点（不输出超链接语法，链接文本会被保留但 URL 会丢失）。
- **块级与强调标签**（`<h1>`–`<h6>` / `<blockquote>` / `<b>` / `<strong>` / `<u>` / `<header>` / `<figure>` / `<small>` / `<code>` / `<label>`）：
  - 在 Markdown / 文本场景（`_forceText == true`）下只递归取文本，**不输出 Markdown 标记**（不加 `#`、`>`、`**` 等）。
  - 标题真正的 `#` 由 `<pre>` 整体导出，或者来自原 HTML 里的字面 `#` 文本——这也是后续要做 `NormalizeMarkdownHeadings` 的原因。
- **`<div>` / `<p>`**：在内容前后保证有换行（若末尾不是 `\n` 就 `TrimAndAddLineEnding`），中间递归子节点。
- **忽略**：`wiz_tmp_caret`、`#comment`、`<style>`、`<meta>`、`<title>`。
- **其它未知标签**：抛异常，让外层 `ExportAll` 记录错误并跳过该笔记。

> Markdown 模式下没有专门处理 `<ul>` / `<ol>` / `<table>` / `<hr>` 等结构。它们若出现会落进 `default` 分支抛异常，对应的笔记会被记录为错误。当前的 Markdown 路径主要面向「整篇 `<pre>` 包装的 Lite Markdown」以及「以文本为主、带少量图片」的笔记。

### `NormalizeMarkdownHeadings`：修正 ATX 标题写法

由于上面提到的「标题文本里可能掺杂多余空格、尾部 `#`」，写入后再做一次正则规整：

- 匹配 `^(#{1,6})[ \t]*(\S.*?)[ \t]*#*[ \t]*$`，把 `##Title`、`##  Title  ##` 之类统一改写成规范的 `## Title`。
- 用 `FencedCodeRegex` (` ``` ` / `~~~`) 维护 `inFence` 状态，**栅栏代码块内部不做替换**，避免误改正文。
- 只在确实有改动时才回写 `StringBuilder`，避免无谓的内存分配。
- 这里处理的是 ATX 标题（`#` 前缀），Setext（下划线式）标题不在范围内。

### `EnsureEndOfFile`

把末尾多余的 `\r`、`\n`、空格全部 trim 掉，再补一个 `\r\n`（`LineEnding` 常量为 `"\r\n"`），保证文件以一个换行结尾。

## 资源解压：`ExtractIndexFiles`

写出 Markdown 之后会处理 `index_files/`：

- 跳过 `wizEditor*` 开头的编辑器资源。
- Markdown / Text 模式下跳过 `.css`（仅 HTML 模式需要样式表）。
- 目标目录由 `GetPictureDirName(_outputFile, true)` 计算，即 `<标题>.assets/`，与 `<img>` 写入时使用的相对路径一致。
- 文件名直接沿用 zip 内 `index_files/` 之后的路径，按需要建立子目录。

## 写文件与时间戳

- Markdown 内容由 `File.WriteAllText` 以 **UTF-8 without BOM** (`_utf8WithoutBom`) 写出。
- 写入前比较修改时间：若目标文件已存在且其 `LastWriteTime` 比源笔记的 `_modifiedTime` 更新，则跳过并提示 `has been modified, skip it.`，避免覆盖用户手动改过的导出物。
- 写入后通过 `File.SetLastWriteTime` 把目标文件时间回设为源笔记时间，让下一次增量导出依旧能可靠判定。

## 文本 → Markdown 的升级条件

对 `.txt` 笔记以及「先按文本试」的笔记，`ExportText` 调用同一个 `ProcessContent`：

- 一旦遇到 `<img>`，`_hasImg = true`。
- 退出 `ExportText` 后，若 `_hasImg` 为真，则把 `_exportFormat` 改为 `Markdown`，并把 `_outputFile` 的扩展名改为 `.md`（对 `.txt` 笔记，当前代码使用 `Path.ChangeExtension(_outputFile, ".md")` 但未把返回值赋回 `_outputFile`，所以 `.txt` 笔记升级路径下文件名实际仍是原 `.txt`；详见 [Exporter.cs:266](../Exporter.cs#L266)）。

## 图片目录命名

`GetPictureDirName(docFile, isMarkdown, escapeSpace)`：

- 去掉文件名中 `.txt` / `.md` / `.html` 任一扩展名，得到 `titleNoExt`。
- Markdown 模式拼 `<titleNoExt>.assets`，HTML 模式拼 `<titleNoExt>_files`。
- `escapeSpace = true` 时把空格替换成 `%20`，用于写入 `<img>` 的 Markdown 链接。

这与 Typora 等编辑器默认的 `<文件名>.assets/` 约定一致，方便后续在 Markdown 编辑器中直接打开。
