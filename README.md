# 微服务容器编排项目

## Git LFS 安装与使用

本项目使用 Git LFS 管理 `.tar`、`.jar` 等大文件。克隆项目前，请先确认电脑上已经安装并初始化 Git LFS。

### Windows 安装

打开 PowerShell，执行：

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
git clone https://github.com/RankerMaster/weifuwu_bianpai.git
cd weifuwu_bianpai
git lfs pull
```

其中，`git lfs pull` 会下载仓库中由 Git LFS 管理的大文件。

### 已安装 Git LFS 的电脑

如果 `git lfs version` 能正常显示版本号，则不需要重新安装。克隆或更新项目后执行：

```powershell
git lfs pull
```

即可确保本地的大文件内容完整。
