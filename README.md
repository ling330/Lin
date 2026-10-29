# QQ 音乐批量解码工具

Windows 桌面批量解码工具，将 QQ 音乐 **musicex V1** 文件恢复为可播放的原格式音频。使用 C# 和 Windows Forms 编写，运行时不需要 Python 或 Node.js。

## 功能

- 支持 `.mflac → .flac`、`.mgg → .ogg`、`.mmp4 → .m4a`。
- 添加多个文件或整个文件夹，支持拖放和子文件夹扫描。
- 保留原音质与已有标签，不额外转码。
- 保留源文件；同名输出自动编号，避免覆盖。
- 保存处理记录，再次运行时跳过已完成且未变化的文件。
- 逐文件进度、失败原因和 JSON 报告；单个文件失败后继续处理。
- 可停止任务，清理尚未完成的临时输出。
- 可调用本机 FFmpeg 完整解码校验；FLAC 额外核对内嵌音频 MD5。

不支持旧版 QMC / QTag / STag、NCM、KGM，以及尚未下载完成的文件。真实歌曲验证范围目前为 `.mflac`；`.mgg` 和 `.mmp4` 的处理路径已实现，尚未用真实文件验证。

## 环境

- Windows 10 / 11，64 位。
- .NET Framework 4.8。
- 桌面版 QQ 音乐已运行，并登录能访问相应歌曲的账号。
- 获取歌曲密钥时需要联网。
- FFmpeg 可选，用于完整音频校验；本仓库不捆绑 FFmpeg。

## 构建

在 Windows PowerShell 或 PowerShell 中进入仓库目录：

```powershell
.\Build.ps1
```

程序生成到 `artifacts\QQBatchDecoder.exe`。构建脚本直接调用 Windows 的 .NET Framework C# 编译器，不依赖 NuGet 包。

构建并运行离线自检：

```powershell
.\Build.ps1 -RunTests
```

自检结果保存在 `artifacts\self-test-<标识>\report.json`。这些生成物已在 `.gitignore` 中排除。

也可使用 Visual Studio 打开 `QQBatchDecoder.csproj`，安装 .NET Framework 4.8 开发工具后编译。

## 使用

1. 打开桌面版 QQ 音乐并登录。
2. 双击 `artifacts\QQBatchDecoder.exe`。
3. 添加歌曲或文件夹，选择保存位置。
4. 推荐开启完整性校验，然后点击「开始解码」。

工具会查找程序旁边或 `PATH` 中的 `ffmpeg.exe`，也会检查本机剪映的安装目录。未找到时，可在窗口中手动选择校验程序；取消完整性校验后，仅检查音频头，无法确认整首音频内容。

详细操作见 [使用说明](docs/使用说明.txt)。

### 命令行

```powershell
.\artifacts\QQBatchDecoder.exe --cli "D:\歌曲" --output "D:\已解码"
.\artifacts\QQBatchDecoder.exe --cli "D:\歌曲" --output "D:\已解码" --ffmpeg "D:\工具\ffmpeg.exe"
.\artifacts\QQBatchDecoder.exe --cli "D:\歌曲" --output "D:\已解码" --no-verify
```

可加 `--no-recursive` 关闭子目录扫描，或 `--report "D:\本次报告.json"` 另外保存一份报告。

退出码：`0` 全部成功或跳过，`1` 存在失败或取消，`2` 参数或启动错误。PowerShell 调用窗口程序时，若需要等待退出，可使用 `Start-Process -Wait -PassThru`。

## 数据处理

工具在本机读取 QQ 音乐进程中的相关会话信息，仅向 QQ 音乐官方服务请求对应歌曲的密钥；音频文件不上传。会话信息与歌曲密钥只保存在运行时内存，不写入处理记录或报告。

输出目录中的 `.qqdecode-history.json` 保存源文件路径、大小、修改时间和输出状态，用于识别已完成的任务。JSON 报告包含本机文件路径及处理结果。它们均不应随源码上传，仓库的忽略规则已排除这些运行数据。

客户端或服务接口更新可能影响自动获取密钥。工具不提供登录、购买或音乐下载功能。

## 源码结构

```text
QQBatchDecoder.csproj          Visual Studio / MSBuild 项目
Build.ps1                     构建与离线自检
source/
  QQBatchDecoder.cs           界面、批处理、密钥请求、QMC2、校验
  SelfTest.cs                 参考向量及文件处理测试
  AssemblyInfo.cs             程序版本信息
  test-vectors.json           确定性生成的合成测试数据
docs/                         使用说明和验证记录
LICENSE                       本项目 MIT 许可证
THIRD-PARTY-NOTICES.txt        上游代码声明
```

测试向量中的密钥与会话字符串均为合成数据，不是用户账号或实际歌曲的密钥。

## 验证情况

- 16 组 RC4 / Map 参考向量，以及非对齐分块和边界测试。
- 6 组 Raw / EncV1 / EncV2 密钥推导测试。
- musicex 文件尾、中文路径、递归扫描和重复导入。
- 两首真实 `.mflac` 的完整解码与 FLAC 音频 MD5 校验。
- 重复运行跳过、同名保护、损坏文件检出和失败临时输出清理。

完整记录和界面检查的限制见 [验证记录](docs/验证记录.txt)。

## 许可与致谢

本项目按 [MIT](LICENSE) 许可提供。QMC2 算法及 musicex 参考来自 [QM Unlock](https://github.com/mary20050520/qmunlock)，其版权及 MIT 许可全文保留在 [第三方声明](THIRD-PARTY-NOTICES.txt) 中。

FFmpeg 不属于本仓库分发内容；使用本机 FFmpeg 时，其许可证仍适用于该安装。
