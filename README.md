# vnavmesh-tool

上游项目：

- 国际服版 vnavmesh（awgil/ffxiv_navmesh）：https://github.com/awgil/ffxiv_navmesh
- 中国服版 vnavmesh（AtmoOmen/ffxiv_navmesh-cn）：https://github.com/AtmoOmen/ffxiv_navmesh-cn

在卫月（Dalamud）框架内一键切换 **国际服版 vnavmesh** 与 **中国服版 vnavmesh** 的小工具。

两个版本的 vnavmesh 命名空间都是 `vnavmesh`，同名 IPC 与同名静态注册在同一个游戏进程里会互撞，一旦同时加载就会报错，且本次进程内再也无法加载。所以本工具不是「直接加载另一个」，而是：

1. 先把**所有** InternalName 为 `vnavmesh` 的插件条目（国际服版、中国服版、以及 `installedPlugins` 里那份）逐一卸载；
2. 轮询插件状态，直到全部条目都真正落到 `Unloaded`，且没有任何条目处于 `Loading` / `Unloading` / `UnloadError`；
3. 二次复核一遍，任何一条还没关干净就直接中止，**绝不发起加载**；
4. 只有复核通过，才加载目标版本，并把结果写回配置，让重启后仍只启用目标版本。

`await UnloadAsync()` 在 Dalamud 内部会等 `PluginWaitBeforeFree`（默认 1000 ms）后才真正释放 AssemblyLoadContext，所以这里等到的「关闭完成」是插件实例、ALC 都已释放的完成态，不是仅发出关闭请求。

## 目录结构

```
Dev/
├── vnavmesh-tool.dll            插件本体
├── vnavmesh-tool.json           插件清单（InternalName: vnavmesh-tool）
├── vnavmesh-tool.deps.json
├── RegisterDevPlugins.ps1       注册脚本（改 dalamudConfig.json）
├── RegisterDevPlugins.bat       双击运行上面的脚本
├── README.md
└── Versions/
    ├── Global/                  国际服版 vnavmesh（含 DotRecast / SharpDX 依赖）
    │   ├── vnavmesh.dll
    │   ├── vnavmesh.json
    │   └── ...
    └── CN/                      中国服版 vnavmesh（含 vnavmesh.Common / vnavmesh.Worker）
        ├── vnavmesh.dll
        ├── vnavmesh.json
        └── ...
```

`Versions\Global` 与 `Versions\CN` 各自只保留**一个最新版本**，目录名不带版本号；实际版本号只在本插件界面和 `/xlplugins` 里显示。两个槽位作为两条独立的**开发插件**注册给 Dalamud，因此各自拥有独立的 `WorkingPluginId`，可以同时存在于插件列表里而互不覆盖。

> 为什么不直接用 `installedPlugins\vnavmesh` 里并存的两个版本目录：Dalamud 启动时会对同一个 `installedPlugins\<InternalName>` 下的版本目录排序，只保留最高版本，其余目录**会被删除**（`PluginManager.CleanupPlugins`）。开发插件路径不受该清理影响，是两版长期共存的唯一稳妥方式。

## 安装

1. **完全退出游戏**（含 `ffxiv_dx11.exe`）。
2. 双击 `RegisterDevPlugins.bat`（或右键 PowerShell 运行 `RegisterDevPlugins.ps1`）。
   脚本会把下面三条路径插入 `%APPDATA%\XIVLauncherCN\dalamudConfig.json` 的 `DevPluginLoadLocations`，并在同目录留一份 `.bak-时间戳` 备份：

   ```
   ...\vnavmesh-tool\Dev\vnavmesh-tool.dll
   ...\vnavmesh-tool\Dev\Versions\Global\vnavmesh.dll
   ...\vnavmesh-tool\Dev\Versions\CN\vnavmesh.dll
   ```

   旧版留下的 `Versions\<flavor>-<版本号>\vnavmesh.dll` 条目会被一并清掉。脚本会在游戏仍在运行时拒绝执行（退出码 2），因为 Dalamud 退出游戏时会把内存里的配置回写，覆盖掉手改的内容。
3. 启动游戏，`/xlplugins` 里应能看到 `vnavmesh-tool`、`vnavmesh Global`、`vnavmesh CN` 三条开发插件。启用 `vnavmesh-tool`。

不想用脚本也行：`/xlsettings` → `DevPlugins` 分页里手动把上面三个 dll 路径加进去，效果一致。

> 忘了跑脚本也没关系：点任意一种切换时，插件会自己清理指向已删目录的失效条目，并把 `Versions\Global` / `Versions\CN` 补登记为开发插件再重新扫描，不需要重启游戏。

## 使用

窗口：`/vnavmeshtool`

| 按钮 | 命令 | 行为 |
| --- | --- | --- |
| 切换国际服版和中国服版 | `/vnavmeshswitch` | 检测当前已加载的版本，关掉它，再开启另一个版本 |
| 开启国际服版 | `/vnavmeshglobal` | 关掉当前已加载的任何版本，开启国际服版 |
| 开启中国服版 | `/vnavmeshchina` | 关掉当前已加载的任何版本，开启中国服版 |

三条命令都可以直接写进游戏宏：

```
/vnavmeshswitch
/vnavmeshglobal
/vnavmeshchina
```

窗口里会实时列出每个 vnavmesh 条目的状态与版本号（已加载 / 加载中 / 已关闭 / 卸载失败 …），切换过程中按钮会禁用，避免并发操作。指向已删除目录的历史条目会显示为「路径失效，已忽略」，不参与切换判定，并在下一次操作时被自动清理。

## 持久化

切换成功后，工具会：

- 把目标条目的 `StartOnBoot` 置为 `true`、其余 vnavmesh 条目置为 `false`；
- 在 `DefaultProfile` 里写入各条目的启用状态（目标为 `true`，其余为 `false`，`apply: false` 不触发二次加载）。

这样重启游戏时不会出现两个版本同时被自动加载的情况。注意 `installedPlugins\vnavmesh` 里那份也会被一并写成禁用——它和开发插件里那份是同一版本，重复启用没有意义，也最容易撞车。

## 导入 / 更新

窗口底部「导入 / 更新 vnavmesh」折叠区有两个按钮：

- **扫描并导入新版本 vnavmesh**：扫描 `%APPDATA%\XIVLauncherCN\installedPlugins\vnavmesh\*`（以及国际服启动器的同名目录），分别识别国际服版与中国服版，与 `Versions\Global` / `Versions\CN` 里的版本号对比；本地库里的版本更新时，用它替换对应槽位。槽位里原本在运行的插件会被安全卸载、替换文件后再重新加载。
- **直接线上更新 vnavmesh**：直接读取上游插件库（国际服 `puni.sh/api/repository/veyn`、中国服 `AtmoOmen/DalamudPlugins` 的 `pluginmaster.json`），按 `InternalName == "vnavmesh"` 找到每个库里的最新版本与下载链接；版本更新时下载 zip、解压到对应槽位、更新完成后删除临时文件。走系统代理（`127.0.0.1:10853` / `10852` / `7890`）作为直连失败时的兜底。

两个按钮都只动 `Versions\Global` / `Versions\CN`，不会碰 `installedPlugins` 里的副本。导入或更新后建议执行任意一种切换，让插件重新接管 vnavmesh 的开关状态。

## 卸载

1. `/xlplugins` 里禁用并删除 `vnavmesh-tool`、`vnavmesh Global`、`vnavmesh CN`。
2. `/xlsettings` → `DevPlugins` 里删掉对应三条路径。
3. 删除本目录。

`installedPlugins\vnavmesh` 下被本工具写成禁用的那份，如果之后要恢复官方加载，手动在 `/xlplugins` 里重新启用即可。

## 已知边界

- 本工具通过反射调用 Dalamud 内部的 `PluginManager` / `LocalPlugin.LoadAsync` / `UnloadAsync`，属于内部 API；Dalamud 大版本更新若改动这些成员，需要同步调整（失败时会打印明确错误，不会静默撞车）。
- 切换到一半报 `Plugin DLL file at '…' did not exist, cannot load.`：说明开发插件列表里留下了指向已删目录的旧条目（多见于 `Versions` 目录改名之前登记的路径）。插件的自愈逻辑会在下一次操作时清掉它们；如果仍报同样的错，完全退出游戏后跑一次 `RegisterDevPlugins.bat`。
- 失效条目在**当前**游戏进程内无法彻底从列表里消失：Dalamud 只在启动时构建插件条目，插件只能把它们标记为「路径失效，已忽略」并从开发插件配置里移除，重启后就不再出现。
- 切换过程不做超时后的「强行加载」兜底：卸载 150 s 内没关干净就报错中止。宁可你手动点第二次，也不冒同名冲突的风险。
- 两个版本的实际寻路算法差异来自 vnavmesh 自身（国际服版为进程内 DLL 实现，中国服版带 `vnavmesh.Worker.exe` 子进程），本工具只负责让它们互斥地加载。
- 线上更新依赖上游库的 `DownloadLinkInstall` 字段；上游若改字段名或仓库地址，更新按钮会报错并保留原槽位内容不动。
