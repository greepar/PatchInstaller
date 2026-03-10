# PatchInstaller

Steam补丁安装器模板。

![demo](demo.webp)

## 配置

修改模板只需要动一个文件：
[PatchInstaller/Build/InstallerConfig.props](PatchInstaller/Build/InstallerConfig.props)


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
  控制窗口标题、界面大标题
- `InstallerDefaultPatchUrl`
  默认补丁直链，可以留空
- `InstallerPatchFilePrefix`
  自动扫描同文目录下的文件前缀(补丁文件)
- `InstallerSteamGameFolderName`
  Steam游戏目录名，用于自动定位 Steam 游戏目录

## 可选：修改图标

默认图标文件在：
[PatchInstaller/Assets/avalonia-logo.ico](PatchInstaller/Assets/avalonia-logo.ico)

如果你要换图标：

1. 准备一个 `.ico` 文件,并替换它。

## 构建方法

### 方法一：Fork 后改 `props`，用 GitHub Actions 构建

适合不想在本地配环境。

步骤：

1. Fork 本仓库
2. 修改 [InstallerConfig.props](PatchInstaller/Build/InstallerConfig.props)
3. 如果需要，再替换图标文件
4. Push 到你自己的仓库
5. GitHub Actions 会自动执行 [.github/workflows/build.yml](.github/workflows/build.yml)
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

## 使用方法

配置完上方信息后，可以按下面两种方式使用补丁安装器。

### 1. 本地补丁自动识别

`InstallerPatchFilePrefix` 配合自动扫描使用。

程序启动后，会自动扫描程序同目录下符合此前缀的补丁压缩包。

使用时只需要把 `.zip` / `.rar` / `.7z` 补丁文件放到程序同目录即可。

### 2. 默认补丁直链自动下载

`InstallerDefaultPatchUrl` 用于配置默认补丁直链。

配置后，单文件程序可以直接按这个链接自动下载补丁并安装。

## 说明

- 下载缓存临时放在 `%TEMP%\PatchInstaller`
- 当前默认支持 `.zip` / `.rar` / `.7z`
