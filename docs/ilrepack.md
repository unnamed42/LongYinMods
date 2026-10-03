# 往 mod 里内嵌第三方 DLL（ILRepack）

> 归属：[`AGENTS.md`](../AGENTS.md) §3.5 的详细展开。
> 本项目实例：`mcs.dll`（Mono.CSharp，用于 REPL）。
> 目标：**仍产出单个 DLL**，不需要用户多拷几个依赖。

---

### 3.5 往 mod 里内嵌第三方 DLL（ILRepack）

有时需要把 MelonLoader **没提供**的库随 mod 一起发布（本项目实例：`mcs.dll` / Mono.CSharp，
用于 REPL）。目标是**仍产出单个 DLL**，不需要用户多拷几个依赖。

MelonLoader 自带的依赖（`Newtonsoft.Json` / `Iced` / `Mono.Cecil` / `Il2CppInterop.*` …）
**不需要内嵌** —— 用 `<Private>false</Private>` 引用即可（运行时已经有）。

配方（已在本项目验证，产出单 DLL）：

```xml
<!-- 1. 把 DLL 收进仓库，例如 lib/net6/mcs.dll -->
<Reference Include="mcs">
  <HintPath>$(LibDir)\net6\mcs.dll</HintPath>
  <Private>true</Private>          <!-- 要拷到输出目录，ILRepack 才能看到它 -->
</Reference>

<PackageReference Include="ILRepack.Lib.MSBuild.Task" Version="2.0.48">
  <PrivateAssets>all</PrivateAssets>
</PackageReference>
```

```xml
<Target Name="MergeThirdParty" AfterTargets="Build" Condition="'$(Configuration)'=='Release'">
  <PropertyGroup>
    <MergedModPath>$(TargetDir)MyMod.dll</MergedModPath>
    <PristineModPath>$(IntermediateOutputPath)MyMod.dll</PristineModPath>
  </PropertyGroup>
  <ItemGroup>
    <MergeInputs Include="$(PristineModPath)" />
    <MergeInputs Include="$(LibDir)\net6\mcs.dll" />
  </ItemGroup>
  <ILRepack TargetKind="SameAsPrimaryAssembly" OutputFile="$(MergedModPath)"
            InputAssemblies="@(MergeInputs)"
            LibraryPath="$(TargetDir);$(LibDir)\net6;$(MelonDir);$(UnityPath)"
            Internalize="false" Parallel="true" DebugInfo="false"
            Union="true" AllowDuplicateResources="true"
            AllowedDuplicateNamespaces="Mono.CompilerServices" />
</Target>
```

**三个必踩的坑**（每一个都真耗过时间）：

1. ⚠️ **必须放一个空的 `<Project>/ILRepack.targets`**。`ILRepack.Lib.MSBuild.Task` 会
   **自动导入**一个 target，它把 `$(OutputPath)*.dll` **全部**合并。你已经有自己的合并步骤时，
   它会把自己的产物**再合一次** → `Duplicate type ... was also present in ...`。
   该自动 target 的条件是 `!Exists('$(ILRepackTargetsFile)')`，所以**放一个空文件即可关掉它**。
2. ⚠️ **合并的输入必须是 `$(IntermediateOutputPath)MyMod.dll`（obj 里的原始产物），
   不能是 `$(TargetPath)`**。读 `TargetPath` 会让增量构建把「已合并的 DLL」当成输入再合一次，
   同样报重复类型 —— 而且**清一次 obj/bin 后会“好一次”**，极容易被误判成偶然问题。
3. ⚠️ **`TargetKind` 用 `SameAsPrimaryAssembly`**，写 `Library` 会得到 MSB4064 警告。

**参数名以实际暴露的为准** —— 猜错会报 `MSB4064: 不支持"xxx"参数`。
本项目实测 `ILRepack.Lib.MSBuild.Task` 2.0.48 暴露的可写属性（用
`monodis --property <task.dll>` 枚举，不要猜）：

```
AllowDuplicateResources  AllowedDuplicateNamespaces  Internalize  InternalizeExclude
InternalizeAssembly  RenameInternalized  ExcludeInternalizeSerializable  Union
TargetKind  OutputFile  InputAssemblies  LibraryPath  Parallel  DebugInfo
KeyFile  DelaySign  XmlDocumentation  RepackDropAttribute  …
```

**不暴露**（写了必报 MSB4064）：`AllowedDuplicateTypes`、`UnionMerge`、`AllowZeroInputAssemblies`。
注意正确拼写是 `AllowedDuplicateNamespaces`（Duplicate 是复数）。

**验证合并成功**（不是看构建退出码）：

```bash
# 1) 第三方类型真的进去了
monodis --typedef MyMod/bin/Release/net6.0/MyMod.dll | grep -c "Mono.CSharp"
# 2) 它已不再是外部引用（应该没有输出）
monodis --assemblyref MyMod/bin/Release/net6.0/MyMod.dll | grep -i mcs
```

> 补充：被内嵌的 DLL 本身可能**已经是别人的合并产物**（`mcs.dll` 就内含 MonoMod，
> 自带一份 `Mono.CompilerServices.SymbolWriter`）。这就是 `Union="true"` +
> `AllowedDuplicateNamespaces` 这两个参数存在的原因 —— 不能省。

**NuGet 缓存重定向**：若 `~/.nuget` 或 `~/.local/share/NuGet` 在只读/受限位置，
在仓库根放 `nuget.config`：

```xml
<configuration>
  <config>
    <add key="globalPackagesFolder" value=".nuget/packages" />
    <add key="http_cache_path" value=".nuget/http-cache" />
  </config>
</configuration>
```
