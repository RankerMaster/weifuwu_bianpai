# 微服务容器编排项目

本项目提供基于 Windows WPF 的桌面管理界面，通过 Docker 和 SSH 完成远程设备信息读取、镜像构建与同步、容器管理及预部署策略生成。

## 交接分支与阅读顺序

当前主要修改位于 [`codex/iamges-container-improve-v1`](https://github.com/RankerMaster/weifuwu_bianpai/tree/codex/iamges-container-improve-v1) 分支。接手本次改进版本时，请使用该分支，不要只克隆默认分支后直接开始开发。

- 软件功能与使用说明可以查看 [Excel 功能清单](微服务编排管理软件功能清单.xlsx) 或 [Markdown 使用说明与项目交接](软件使用说明.md)。
- [后续开发清单](后续开发清单.md)：需要补充、修改和优化的任务，以及各项完成标准。

交接以已提交并推送到该分支的内容为准，本地未提交或未推送的修改不会随克隆获得。

## Git LFS 安装与使用

本项目使用 Git LFS 管理 `.tar`、`.jar` 等大文件。克隆项目前，请先确认电脑上已经安装并初始化 Git LFS。

### Windows 安装

先确认电脑已安装 Git，再打开 PowerShell，执行：

```powershell
winget install GitHub.GitLFS
git lfs install
```

安装完成后，可以使用以下命令确认版本：

```powershell
git lfs version
```

### 克隆项目

```powershell
git clone --branch codex/iamges-container-improve-v1 https://github.com/RankerMaster/weifuwu_bianpai.git
cd weifuwu_bianpai
```

提前完成 Git LFS 安装和初始化后，正常克隆会自动下载当前版本所需的大文件。

### 大文件下载失败或缺失时

如果克隆过程中大文件下载失败，或大文件仍是很小的文本占位文件，在仓库目录中补执行：

```powershell
git lfs pull
```

`git lfs install` 用于初始化 Git LFS；`git lfs pull` 用于补下载当前版本的大文件，不是每次克隆后都必须执行。

## 协作开发与提交方式

 <font color="red">`codex/iamges-container-improve-v1` 作为本次交接和后续合并的基准分支。接手同学应从该分支创建自己的功能分支，在功能分支上修改，通过Pull Request（PR）审核后由审核人员合并回基准分支，不直接向基准分支推送修改。</font>

### 1. 创建自己的功能分支

完成上面的指定分支克隆步骤后，在仓库目录中执行：

```powershell
git switch -c dev/xxx
```

<font color="red">`xxx` 为接手同学的名字小写，比如张三，就叫dev/zhangsan；创建后，后续修改和提交都在自己的功能分支上进行。</font>

### 2. 修改、验证并推送功能分支

完成修改后，运行与改动相关的构建或测试，检查差异，再提交实际需要的文件：

```powershell
git status
git diff
```

下面以修改 `WpfApp1/MainWindow.Strategy.cs` 为例，当前同学的分支为dev/zhangsan，文件路径、提交说明和分支名应替换为本次任务的实际内容：

```powershell
git add WpfApp1/MainWindow.Strategy.cs
git commit -m "完成了策略模块的修改，优化了部分逻辑功能"
git push -u origin dev/zhangsan
```

仅提交本次任务涉及的源码和文档，不提交密码、密钥、本地日志、缓存或编译产物。推送到原仓库需要负责人授予写入权限；没有权限时，先联系负责人确认协作方式。

### 3. 创建 PR 并由负责人审核

在 GitHub 仓库中创建 Pull Request，确认分支选择为：

- **base（目标分支）**：`codex/iamges-container-improve-v1`。
- **compare（来源分支）**：自己的功能分支，例如 `dev/zhangsan`。

PR 中说明修改目的、主要改动、验证结果及未验证内容，**由负责人审核后合并**。后续新任务从更新后的基准分支另建功能分支。

**以上是项目约定。**

## 开发与运行环境

- Windows，支持 .NET 桌面开发的 Visual Studio，以及 .NET Framework 4.8 目标包。
- Docker CLI 和可用的本地 Docker 引擎，用于本地镜像操作。
- OpenSSH 客户端，确保 `ssh`、`scp` 可用。
- 使用策略功能时，需要 Python 3 和 `numpy`。
- 远程设备需要启用 SSH、运行 Docker，并具备相应操作权限和足够的可用空间；镜像需与设备架构兼容。

使用 Visual Studio 打开根目录的 `WpfApp1.sln`，选择 Debug 或 Release、Any CPU，构建并启动 `WpfApp1` 项目。配套程序包、模板和脚本的目录要求见[软件使用说明](软件使用说明.md)。

## 主要目录

| 路径 | 用途 |
| --- | --- |
| `WpfApp1.sln` | 桌面项目解决方案入口 |
| `WpfApp1/` | WPF 界面与主要业务代码 |
| `dockerbuild_workspace/program_package/` | 配套程序包 |
| `dockerbuild_workspace/template_dockfile/` | Dockerfile 模板，保留项目现有拼写 |
| `cuckoo_Train1.py` | 策略脚本；实际算法以调用入口为准 |
| `bond_demo_v4_2/bond_demo_v4/` | 配套建模与仿真服务源码 |

## 接手时的验证重点

先在测试设备上跑通“读取设备 → 构建或导入镜像 → 同步镜像 → 创建并启动容器 → 查看状态与日志”的流程，再开展后续修改。

当前“算法部署”只准备本地 JAR 和 Dockerfile；“模型部署”完成文件写入后，仍需验证应用是否成功加载模型；策略页面的“内存阈值”实际用于镜像大小预算。具体边界和后续任务见上述两份文档，不能仅凭按钮名称判断功能已完成。
