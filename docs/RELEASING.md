# 发布流程（维护者）

面向 v1.0.0 之后的所有版本。整个流程都在 Windows 上执行。

## 1. 准备版本

1. 修改 `Directory.Build.props` 里的 `<Version>`（例如 `1.0.1`）。
2. 在 `CHANGELOG.md` 顶部加一节 `## [1.0.1] - YYYY-MM-DD`。
3. 跑通构建与测试：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1
   ```

4. 提交流程改动：

   ```powershell
   git add -A
   git commit -m "Release 1.0.1"
   ```

## 2. 本地打包验证

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release.ps1
```

产物在 `artifacts\release\`：

| 文件 | 说明 |
| --- | --- |
| `DownCraft-<版本>-win-x64.zip` | 自包含，目标机不需要安装 .NET |
| `DownCraft-<版本>-win-x64-lite.zip` | 框架依赖，需要 .NET 10 Desktop Runtime |
| `SHA256SUMS.txt` | 两个包的 SHA-256 |

每个压缩包里是一个 `DownCraft\` 目录：`downcraft.exe`、`DownCraft.Gui.exe`、
`downcraft_native.dll`、`README.md`、`LICENSE`、`CHANGELOG.md`。

可选参数：

```powershell
-SkipNative        # 跳过 C++ 构建（包里将缺原生 DLL，运行时自动用托管回退）
-NoSelfContained   # 只产出自包含包之外的部分（与 -SkipLite 组合可只要一个包）
-SkipLite          # 不产出框架依赖包
-Runtime win-arm64 # 交叉发布到 ARM64
```

## 3. 打标签并发布

标签必须与 `<Version>` 完全一致（`v1.0.1`），CI 会校验，不一致直接失败：

```powershell
git tag v1.0.1
git push origin main --tags
```

推 tag 会触发 `.github/workflows/release.yml`：

1. `windows-latest` 上构建解决方案并跑 148 个单元测试；
2. 校验 tag 与 `Directory.Build.props` 的版本一致；
3. 调 `scripts/release.ps1` 生成两个包与校验和；
4. 解压自包含包，运行 `downcraft.exe version` 冒烟验证；
5. 用 `softprops/action-gh-release` 创建 Release 并上传 `*.zip` + `SHA256SUMS.txt`。

## 4. 发布后检查

- Release 页面有两个 zip 和 `SHA256SUMS.txt`；
- 下载 `DownCraft-<版本>-win-x64.zip`，解压后 `downcraft.exe doctor` 正常；
- GUI 能打开、能看到版本列表；
- README 顶部的下载说明与实际资产名一致。

## 回滚

Release 页面删除对应 Release 与 tag 即可；`gh release delete v1.0.1 --cleanup-tag` 一条命令完成。
