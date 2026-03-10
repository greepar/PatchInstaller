# PatchInstaller

基于 Avalonia + NativeAOT 的补丁安装器模板。

你主要只需要改一个文件：
[PatchInstaller/Build/InstallerConfig.props](/C:/Users/greep/Downloads/1/PatchInstaller/PatchInstaller/Build/InstallerConfig.props)

## 配置

`InstallerConfig.props` 当前模板：

```xml
<Project>
  <PropertyGroup>
    <AssemblyName>PatchInstaller</AssemblyName>
    <InstallerProductName>PatchInstaller</InstallerProductName>
    <InstallerDefaultPatchUrl>https://patch.qwq.lu/kiss</InstallerDefaultPatchUrl>
    <InstallerPatchFilePrefix>KissMeEveryday</InstallerPatchFilePrefix>
    <InstallerSteamGameFolderName>Mainichikisushite</InstallerSteamGameFolderName>
  </PropertyGroup>
</Project>
```

各字段含义：

- `AssemblyName`
  控制最终生成的程序文件名，例如 `PatchInstaller.exe`
- `InstallerProductName`
  控制窗口标题、界面标题、产品名等显示文本
- `InstallerDefaultPatchUrl`
  默认补丁链接，可以留空
- `InstallerPatchFilePrefix`
  自动扫描本地补丁时使用的文件名前缀
- `InstallerSteamGameFolderName`
  用来自动定位 Steam 游戏目录，例如 `steamapps/common/<这里的值>`

示例：

```xml
<Project>
  <PropertyGroup>
    <AssemblyName>MyGamePatchInstaller</AssemblyName>
    <InstallerProductName>我的游戏补丁安装器</InstallerProductName>
    <InstallerDefaultPatchUrl>https://example.com/patch/latest.zip</InstallerDefaultPatchUrl>
    <InstallerPatchFilePrefix>MyGamePatch</InstallerPatchFilePrefix>
    <InstallerSteamGameFolderName>MyGame</InstallerSteamGameFolderName>
  </PropertyGroup>
</Project>
```

## 可选：修改图标

默认图标文件在：
[PatchInstaller/Assets/avalonia-logo.ico](/C:/Users/greep/Downloads/1/PatchInstaller/PatchInstaller/Assets/avalonia-logo.ico)

如果你要换图标：

1. 准备一个 `.ico` 文件
2. 替换这个文件，或者改 [PatchInstaller.csproj](/C:/Users/greep/Downloads/1/PatchInstaller/PatchInstaller/PatchInstaller.csproj) 里的 `ApplicationIcon`

当前配置是：

```xml
<ApplicationIcon>Assets\avalonia-logo.ico</ApplicationIcon>
```

如果你改了文件名，比如换成 `Assets\my-icon.ico`，就同步改成：

```xml
<ApplicationIcon>Assets\my-icon.ico</ApplicationIcon>
```

## 构建方法

### 方法一：Fork 后改 `props`，用 GitHub Actions 构建

适合不想在本地配环境。

步骤：

1. Fork 本仓库
2. 修改 [InstallerConfig.props](/C:/Users/greep/Downloads/1/PatchInstaller/PatchInstaller/Build/InstallerConfig.props)
3. 如果需要，再替换图标文件
4. Push 到你自己的仓库
5. GitHub Actions 会自动执行 [.github/workflows/build.yml](/C:/Users/greep/Downloads/1/PatchInstaller/.github/workflows/build.yml)
6. 在 Actions 的构建产物里下载：
   - `PatchInstaller.7z`
   - `PatchInstaller.UPX.7z`

### 方法二：下载到电脑上，本地 `dotnet publish` 构建

适合需要自己调试或本地直接出包。

要求：

- Windows
- .NET 10 SDK
- Visual Studio C++ 工具链 / Build Tools

命令：

```powershell
dotnet publish .\PatchInstaller\PatchInstaller.csproj -c Release -r win-x64
```

发布产物目录：

```powershell
.\PatchInstaller\bin\Release\net10.0\win-x64\publish
```

主程序通常是：

```powershell
.\PatchInstaller\bin\Release\net10.0\win-x64\publish\PatchInstaller.exe
```

## 说明

- 下载缓存临时放在 `%TEMP%\PatchInstaller`
- 下载成功后只保留程序同目录下的最终补丁文件
- 下载失败或取消会自动清理临时文件
- 当前默认支持 `.zip` / `.rar` / `.7z`
