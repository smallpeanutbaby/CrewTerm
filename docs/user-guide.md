# CrewTerm 工作台使用手册

本文按界面区域介绍 CrewTerm 工作台（`crewterm` 打开的全屏界面）的日常用法，面向已经能启动工作台、想系统了解各项功能的使用者。
安装与第一次启动见 [快速入门](getting-started.md)，全部按键、命令面板命令与右键菜单的速查表见 [快捷键与命令参考](commands.md)。

## 目录

- [1. 界面组成](#1-界面组成)
- [2. 对话框、选择器与命令面板](#2-对话框选择器与命令面板)
- [3. 项目](#3-项目)
- [4. 启动 Agent 或自定义终端](#4-启动-agent-或自定义终端)
- [5. 标签页与分屏](#5-标签页与分屏)
- [6. 终端交互](#6-终端交互)
- [7. Agent 列表与协作](#7-agent-列表与协作)
- [8. 文件区与编辑器](#8-文件区与编辑器)
- [9. Git 与 worktree](#9-git-与-worktree)
- [10. 后台任务与流水线](#10-后台任务与流水线)
- [11. 文件检查点](#11-文件检查点)
- [12. 本地插件](#12-本地插件)
- [13. 设置](#13-设置)
- [14. 分离、停止与重新连接](#14-分离停止与重新连接)

## 1. 界面组成

工作台本身不运行任何终端进程：所有会话都由后台服务（daemon）持有，界面每 160 毫秒向它拉取会话列表与屏幕画面，
并把键盘、鼠标输入按顺序转发过去。因此关闭或分离界面不会结束会话。主窗口的布局如下（为便于对齐，图中用 `*` 代表 `●`、
`[=]` 代表 `[≡]`、`-` 代表 `·`）：

```
+- (A) CrewTerm - MULTI-AGENT WORKSPACE -----------------------------------------------------------+
|+- (B) SPACES - F2 -------+ (D) * Terminal  Review                               [+] [=]  2 panes |
|| * my-app                |+- (E) Claude Code - WORK - 3f9a1c -+|+- (E) Shell - IDLE - 7b2e0d ---+|
||   api-server            ||                                   |||                               ||
||                         || $ claude                          |||                               ||
|+-------------------------+|                                   |||                               ||
|+- (C) AGENTS - F3 -------+|                                   |||                               ||
|| [WAIT] Reviewer         |+-----------------------------------+|+-------------------------------+|
|| [DONE*] Codex           |+- (F) FILES - / ------------++- (G) CODE - F5 Save ------------------+|
|| [WORK] Claude Code      || ../                        ||   1  using System;                    ||
|| [IDLE] Shell            || > src                      ||   2                                   ||
||                         ||   README.md                ||   3  namespace Demo;                  ||
|+-------------------------++----------------------------++---------------------------------------+|
| (H) Started Claude Code in /home/me/my-app                                                       |
| (I) F1 Help  F2 Project  F3 Agent  F4 Files  F5 Save  F6 Git  F7 Tasks  F8 Split  ...            |
+--------------------------------------------------------------------------------------------------+
```

| 标记 | 区域 | 位置 | 作用 | 聚焦方式 |
| --- | --- | --- | --- | --- |
| A | 窗口标题 | 顶部 | 正常为 `CrewTerm  ·  MULTI-AGENT WORKSPACE`；连接旧版后台服务时为 `CrewTerm  ·  OLD SERVER · F1 for upgrade instructions` | — |
| B | 项目列表 `SPACES  ·  F2` | 左上，宽 28 列，约占 32% 高度 | 已登记的项目，当前项目前显示 `●` | 鼠标点击 |
| C | Agent 列表 `AGENTS  ·  F3` | 左下 | 后台服务中的全部会话（不分项目），每行为 `[状态] 名称` | Alt+5 |
| D | 标签栏 | 终端区第一行 | 当前项目的标签页（活动标签前显示 `●`）、`[+]` 新建标签、`[≡]` 打开命令面板、当前标签页的面板数 | 鼠标点击 |
| E | 终端分屏区 | 右上；显示文件区时占 53% 高度，隐藏时延伸到底部两行之上 | 当前标签页的面板，每个面板显示一个会话 | Alt+1 / Alt+2、鼠标点击 |
| F | 文件列表 `FILES  ·  目录` | 终端区下方左侧，宽 30 列 | 当前项目的目录浏览 | Alt+4 |
| G | 编辑器 | 文件列表右侧 | 编辑打开的文件；标题为文件名加 `  ·  F5 Save`，有未保存修改时前缀 `* ` | Alt+3 |
| H | 状态栏 | 倒数第二行 | 操作结果、错误与 Agent 状态提醒 | — |
| I | 快捷键提示 | 最后一行 | `F1 Help  F2 Project  F3 Agent  F4 Files  F5 Save  F6 Git  F7 Tasks  F8 Split  F9 Commands  F10 Detach` | — |

- **F4** 显示或隐藏文件区与编辑器，同时把焦点移到第一个终端面板；**Alt+3**、**Alt+4** 会先把隐藏的文件区显示出来再聚焦。
- **Alt+1** 聚焦当前标签页中视觉顺序（从左到右、从上到下）的第一个面板，**Alt+2** 聚焦第二个（只有一个面板时不动）。
- 项目列表没有聚焦快捷键，用鼠标点击即可。
- 状态栏初始内容：没有项目时为 `F2 Add a project, then F3 start an agent. F9 lists all commands.`，否则为当前项目路径；
  连接旧版后台服务时显示兼容性警告。刷新失败时显示 `Server: ` 加错误信息。
- 建议宿主终端窗口至少 120 列 × 35 行（见 [README](../README.md#前置条件)）。

## 2. 对话框、选择器与命令面板

工作台的所有操作都通过几种模态对话框完成。对话框打开期间全局快捷键（F1–F10、Alt+数字等）不起作用，先关闭对话框。

| 对话框 | 操作 |
| --- | --- |
| 输入框 | 在输入框中按 Enter 等同于 `OK`；Esc 或 `Cancel` 取消。多数输入框留空确认也等同于取消。 |
| 选择器（可过滤列表） | 直接输入文字过滤（不区分大小写的子串匹配，保持原顺序，过滤后默认选中第一项）；↓ 进入列表；Enter 或 `Select` 选择；Esc 或 `Cancel` 取消。列表为空时只弹出 `No entries.` |
| 确认框 | 按钮为 `Confirm` 与 `Cancel`。**默认按钮是 `Cancel`，直接按 Enter 或 Esc 都是取消**；要执行时按 Alt+O 或点击 `Confirm`；Alt+C 与 `Unsaved changes` 中一样表示取消。 |
| 未保存修改 | 标题 `Unsaved changes`，按钮 `Save` / `Discard` / `Cancel`，热键 Alt+S / Alt+D / Alt+C；默认按钮是 `Cancel`。 |
| 文本查看器 | 只读显示长文本（帮助、diff、滚动历史、任务与插件输出等），可滚动、用 Shift+方向键选择并按 Ctrl+C 复制；`Close` 或 Esc 关闭。 |
| 错误提示 | 任何操作失败都以标题为 `CrewTerm` 的消息框显示错误信息，按 `OK` 关闭，界面继续运行。 |

命令、会话名、消息与插件输出等外部文本在显示前会去掉终端转义序列与控制字符。

**命令面板**（F9、Ctrl+Shift+P，或点击标签栏的 `[≡]`）列出全部内置命令和已启用插件提供的命令，标题为
`COMMANDS · type to filter`，用法与选择器相同：输入命令名中的任意片段过滤，Enter 立即执行。过滤只匹配显示的命令名。
全部命令见 [命令参考](commands.md#3-命令面板命令)。

**F1** 打开 `SHORTCUTS · CrewTerm` 帮助窗口，内容包括命令行用法、快捷键提示与会话生命周期说明；连接旧版后台服务时，
兼容性警告显示在最前面。

## 3. 项目

项目就是一个本地文件夹。当前项目决定新会话的工作目录、文件区显示的目录、Git 与检查点作用的范围，以及使用哪一套终端布局。

- **添加项目（F2）**：输入一个已有文件夹的路径（默认值为当前项目目录，没有项目时为启动工作台时的当前目录），首尾空白与引号会被去掉。
  文件夹不存在时报错；已经登记过的文件夹直接切换过去。项目名取文件夹名。
- **切换项目**：在项目列表中选中后按 Enter（或双击）。切换前若编辑器有未保存修改，先询问保存、放弃或取消；
  切换时保存当前项目的布局、关闭编辑器、文件列表回到根目录，再载入目标项目的布局。终端会话本身不受影响。
- **自动登记**：在 Agent 列表中打开另一个目录下的会话、在 F6 中打开或创建 worktree、用 `crewterm --project PATH` 启动时，
  对应目录会自动加入项目列表并成为当前项目。
- **移除**：`Project: Remove from sidebar` 只删除侧栏中的登记，磁盘上的文件夹与其中的会话都不受影响。
- **启动时**：工作台回到上次的项目（用 `--project` 启动时为指定的项目），并按该项目保存的布局恢复各面板。
  项目文件夹已被删除或无法访问时工作台仍能打开，文件区标题显示 `unavailable`，状态栏说明原因。

项目列表保存在状态目录的 `projects.json` 中；状态目录的位置与内容见 [配置与数据参考](configuration.md)。

## 4. 启动 Agent 或自定义终端

按 **F3**（或命令面板 `Agent: New embedded terminal`、右键菜单 `Start new Agent or shell`）打开标题为
`New agent · 项目文件夹名` 的选择器。必须先有当前项目，否则提示 `Add or select a project first (F2).`。

列表依次为全部 Agent 配置与最后一项 `Custom command…`。已找到可执行文件的配置显示实际路径，未找到的显示 `not installed`：

| 配置 | 查找的程序 |
| --- | --- |
| Claude Code | `claude` |
| Codex | `codex` |
| OpenCode | `opencode` |
| Gemini CLI | `gemini` |
| GitHub Copilot CLI | `copilot` |
| Shell | Windows：`pwsh`，找不到时为系统自带的 Windows PowerShell（`powershell.exe`）；Linux / macOS：`$SHELL`，未设置时为 `/bin/sh` |
| 自定义配置 | 用 `Agent: Save custom CLI / SSH profile` 保存的配置 |

- 选择已安装的配置时，用找到的完整路径加配置中的参数启动，侧栏名称为配置名。
- 选择 `not installed` 的配置会报错，提示安装该 CLI 并确认它在 `PATH` 中，或添加自定义配置；`crewterm doctor` 可以查看实际查找结果。
- 选择 `Custom command…` 依次询问：
  1. `Custom terminal` / `Command (agent CLI, shell or ssh)`：要运行的命令，默认 Windows 为 `cmd.exe`，
     Linux / macOS 为 `$SHELL`（未设置时 `/bin/sh`）；
  2. `Terminal name` / `Sidebar display name`：侧栏中的显示名，默认与命令相同。
- 任何一步取消都不会启动会话。

| 平台 | 自定义命令示例 |
| --- | --- |
| Windows | `cmd.exe`、`pwsh -NoLogo`、`ssh user@host`、`.\build.cmd` |
| Linux / macOS | `bash`、`zsh`、`ssh user@host`、`./build.sh` |

会话在当前项目根目录中启动，显示到活动面板并获得焦点，状态栏显示 `Started 名称 in 目录`。每个后台服务最多 32 个会话。

命令开头的裸程序名只在 `PATH` 的绝对路径条目中查找，不会查找项目目录；运行项目内的脚本要写成 `./script.sh`、`.\build.cmd`
或绝对路径（Linux / macOS 上还需要执行权限）。

**自定义配置**：`Agent: Save custom CLI / SSH profile` 依次询问显示名、可执行文件名或完整路径（默认 `ssh`）和可选参数，
保存后出现在 F3 列表中。`Agent: Remove custom profile` 删除自定义配置（内置配置不在列表中）。配置保存在状态目录的
`agent-profiles.json`。Claude Code 与 Codex 启动时的状态回调与原生会话识别见 [Agent 集成指南](agent-integration.md)。

## 5. 标签页与分屏

每个项目有自己的一组标签页，每个标签页是一棵可以嵌套的分屏树，每个叶子是一个面板，每个面板显示一个会话。
**关闭面板或标签页只改变布局，不结束任何会话**；要结束会话请用 `Agent: Terminate selected session`（见 [7.8](#78-终止会话)）。

### 5.1 标签页

| 操作 | 入口 | 说明 |
| --- | --- | --- |
| 新建 | 点击标签栏 `[+]`，或 `Tab: New` | 询问名称（默认 `Terminal N`），新标签页只有一个空面板 |
| 重命名 | `Tab: Rename` | 名称为 1–60 个可打印字符 |
| 切换 | 点击标签；`Tab: Next` / `Tab: Previous`（循环）；`Tab: Choose`（列表显示 `名称  ·  N panes`） | — |
| 关闭 | `Tab: Close (keep sessions running)` | 其中的会话继续运行；关闭最后一个标签页时自动新建一个空的 `Terminal` 标签页 |

每个项目最多 16 个标签页。标签栏宽度不够时只显示包含活动标签的一段连续标签，每个标签占 4–24 列。

### 5.2 分屏

| 操作 | 入口 | 说明 |
| --- | --- | --- |
| 四向拆分 | `Terminal: Split right` / `Split left` / `Split down` / `Split up`，或右键菜单 | 把活动面板一分为二，各占一半，新面板位于指定一侧并获得焦点 |
| 继续嵌套 | 聚焦任一面板后再次拆分 | 每个标签页最多 16 个面板 |
| 快速切换 | **F8**（`Terminal: Toggle split`） | 只有一个面板时向右拆分；已有多个面板时把当前标签页收回为第一个面板，其余面板从布局中移除，会话继续运行 |
| 聚焦 | 点击面板；Alt+1 / Alt+2；`Terminal: Focus next pane`（按视觉顺序循环） | — |
| 调整比例 | 用鼠标拖动面板之间的分隔线（`│` 或 `─`） | 比例限制在 0.1–0.9 |
| 关闭面板 | `Terminal: Close pane (keep session running)`，或右键菜单 | 会话继续运行；关闭的是标签页中唯一的面板时整个标签页一起关闭 |

新拆分出的面板会优先显示一个当前标签页尚未显示、工作目录与项目根目录相同的会话；没有这样的会话时为空面板。

### 5.3 给面板选择会话

| 入口 | 行为 |
| --- | --- |
| Agent 列表中按 Enter | 会话已显示在当前标签页的某个面板中时聚焦那个面板，否则替换活动面板显示的会话 |
| `Terminal: Choose session for active pane`，或右键 `Choose existing session` | 从全部会话中选择，行为同上 |
| `Agent: Choose second split terminal` | 选择一个会话放到第二个面板（列表不含第一个面板的会话）；会话已显示时只聚焦；只有一个面板时先向右拆分 |

会话属于另一个项目时，会先切换到该项目（编辑器有未保存修改时先询问）。被换下的会话继续运行，仍在 Agent 列表中。

空面板（标题 `TERMINAL  ·  F3 New`，内容提示 `F3  Start an agent or shell`）会在下一次刷新时自动显示一个本项目中
尚未在当前标签页出现、工作目录与项目根目录相同的会话（没有当前项目时不限目录）。

### 5.4 面板标题

| 标题 | 含义 |
| --- | --- |
| `名称  ·  状态  ·  会话标识前 6 位` | 正常显示的会话，状态标记见 [7.1](#71-状态标记) |
| `TERMINAL  ·  F3 New` | 面板没有绑定会话 |
| `SAVED SESSION  ·  F9 Resume` | 面板绑定的会话已不在后台服务中（例如服务重启后只剩记录），可以用 `Agent: Resume original conversation` 恢复 |

### 5.5 布局如何按项目保存

每个项目单独保存标签页、分屏树、分隔线比例、各面板绑定的会话，以及活动标签页与活动面板；没有当前项目时也有一套单独的布局。
切换项目时先把当前布局记在内存中，在分离（F10）、`Workspace: Stop all sessions and background tasks`、保存或删除命令模板、
修改设置时写入状态目录的 `workspace.json`。下次打开同一项目时按保存的布局恢复。

多个工作台窗口连接同一个状态目录时，后保存的一方如果发现 `workspace.json` 已被另一方修改，会提示
`Layout could not be saved`：确认后不保存本窗口的布局直接分离（源文件的保存不受影响），取消则留在工作台中。

## 6. 终端交互

### 6.1 键盘归属

终端面板获得焦点时，**除下表中的保留键外，所有能编码的按键都发给子程序**，包括 Ctrl+C、Ctrl+Q、Esc、Tab、Enter、
方向键、PageUp / PageDown、F11、F12，以及带 Shift / Ctrl / Alt 修饰的功能键（如 Shift+F5）。

| 保留键 | 工作台中的作用 |
| --- | --- |
| F1–F10（不带修饰键） | 全局快捷键，见 [命令参考](commands.md#1-全局快捷键) |
| Ctrl+Shift+P | 命令面板 |
| Ctrl+Shift+V / Ctrl+Shift+C | 粘贴到活动终端 / 复制活动终端的选区（没有选区时复制可见屏幕） |
| Alt+1 … Alt+5 | 移动焦点 |

- 需要把保留的 F1–F10（以及 F11、F12、Tab、Esc）发给子程序时，用 `Terminal: Send reserved key (F1–F12 / Tab / Esc)`，
  在 `Send key to terminal` 列表中选择，按键发给活动面板的会话。
- 按键编码遵循 xterm 习惯：Ctrl+字母为对应控制字符，Ctrl+Space 为 NUL，Alt+按键前缀 ESC；子程序开启应用光标键模式时，
  方向键改用 `SS3` 序列。完整编码表见 [命令参考](commands.md#21-终端面板)。
- 键入任何内容都会让面板回到最新画面（结束历史浏览并清除选区）。
- 面板没有绑定会话时键入只会在状态栏提示 `F3 starts a terminal.`；会话进程已退出时提示 `Input: Session has exited`。
- 输入按顺序排队发送，队列已满（2048 条）时提示 `Input queue full; input was not sent. Wait and retry.`。

> **macOS**：Alt 组合键需要在 Terminal.app 中开启“将 Option 键用作 Meta 键”（iTerm2 为 “Left Option key: Esc+”），
> 功能键可能需要同时按住 Fn。部分宿主终端自己占用 Ctrl+Shift 组合键（复制、粘贴、命令面板等），这时按键到不了 CrewTerm，
> 请改用 F9 或右键菜单中的对应命令。

### 6.2 鼠标跟踪与 Shift 手势

- 在面板上按下任一鼠标键都会让该面板获得焦点。
- 子程序开启了鼠标跟踪（DECSET 1000、1002 或 1003）时，点击、拖动、滚轮等事件按 SGR 或传统格式转发给子程序；
  拖出面板后按下的键仍归子程序，坐标限制在面板边缘。
- 以下情况事件改由 CrewTerm 在本地处理：子程序没有开启鼠标跟踪；**按住 Shift**；面板正在浏览历史。
  也就是说，在开启了鼠标支持的 Vim、htop 等程序中，用 **Shift+拖动** 选择文本、**Shift+右键** 打开菜单、**Shift+滚轮** 滚动历史。

| 本地鼠标操作 | 效果 |
| --- | --- |
| 左键拖动 | 选择文本，松开时自动复制到剪贴板（状态栏显示 `Selected terminal text copied.`） |
| 左键单击 | 清除已有选区 |
| 滚轮 | 每格滚动 3 行历史；水平滚轮被忽略 |
| 右键 | 打开 `TERMINAL ACTIONS` 菜单（见 [6.4](#64-右键菜单)） |

### 6.3 选择、复制与粘贴

- 选择期间画面冻结在按下鼠标时的快照上，新输出不会让选区跳动；单击面板或键入任何内容即恢复实时画面。
- 选区跨行时保留换行，每行去掉行尾空白；宽字符（中文、emoji）完整复制。
- **Ctrl+Shift+C** / `Terminal: Copy selected text`：复制活动面板的选区；没有选区时改为复制整个可见屏幕。
- `Terminal: Copy visible screen`：复制活动会话当前可见屏幕的纯文本（不含历史），状态栏显示 `Terminal screen copied.`。
- **Ctrl+Shift+V** / `Terminal: Paste clipboard`：把剪贴板文本粘贴到活动面板的会话。
  - 最多 60,000 个字符，超出时报错；
  - 粘贴前去掉 `ESC` 与 C1 控制字符，剪贴板内容无法提前结束 bracketed paste 或注入按键；
  - 子程序开启了 bracketed paste 时整体包裹发送；没有开启而文本含换行或回车时，先弹出 `Multiline paste` 确认框，
    因为换行可能直接执行命令（按 Alt+O 确认粘贴，Enter、Esc 或 Alt+C 取消）。
- 剪贴板不可用时，复制操作提示 `Clipboard unavailable in this terminal`。

### 6.4 右键菜单

在本地鼠标模式下右键面板（子程序跟踪鼠标时用 Shift+右键）会先聚焦该面板，再打开 `TERMINAL ACTIONS` 菜单，菜单中的命令都作用于被点击的面板：

| 菜单项 | 作用 |
| --- | --- |
| `Copy selected text` | 复制选区，没有选区时复制可见屏幕 |
| `Copy visible screen` | 复制可见屏幕 |
| `Paste clipboard` | 粘贴剪贴板 |
| `View / search history` | 打开滚动历史查看器 |
| `Choose existing session` | 为该面板选择一个已有会话 |
| `Start new Agent or shell` | 等同 F3 |
| `Split right` / `Split down` / `Split left` / `Split up` | 拆分该面板 |
| `Open selected file reference` | 在编辑器中打开选中文本里的文件位置（见 [6.6](#66-从-fileline-跳转到代码)） |
| `Close pane (keep session running)` | 关闭该面板，会话继续运行 |

### 6.5 滚动历史与历史查看器

每个会话保留主屏幕最近 2,000 行滚动历史（纯文本，不保留颜色）。备用屏幕（Vim、less 等全屏程序）没有滚动历史。

- **面板内滚动**：本地鼠标模式下滚轮向上浏览历史，画面冻结、不再跟随输出；滚回底部或键入任何内容后恢复实时画面。
  键盘的 PageUp / PageDown 会发给子程序，不滚动本地历史。
- **历史查看器**：`Terminal: View / search scrollback`（右键 `View / search history`）在只读查看器中显示活动会话的全部历史加当前屏幕文本，
  标题为 `SCROLLBACK · Ctrl+F to find`。查看器中可以用方向键、PageUp / PageDown、Ctrl+Home / Ctrl+End 移动，Shift+方向键选择，
  Ctrl+C 复制选区（没有选区时复制光标所在行）；Ctrl+F 打开 `Find in text` 查找框，输入文字回车后从光标处向后查找下一处（不区分大小写）并选中。

### 6.6 从 file:line 跳转到代码

Agent 与编译器输出中的文件位置可以直接在编辑器中打开：

1. 在终端面板中拖动选中文件位置（例如 `src/Parser.cs:42:7`）；
2. 右键选择 `Open selected file reference`。

识别的写法为 `路径:行[:列]` 与 `路径(行[,列])`，路径必须带扩展名，可以是相对项目根目录的路径或绝对路径；
选中文本首尾的空白与引号会被去掉，列号不使用。识别不出行号时把选中文本当作项目内的文件路径直接打开。
文件必须位于当前项目内且不经过符号链接，否则报错。

不在终端中的位置可以用 `Files: Open path / file:line:column` 手动输入，规则相同。

## 7. Agent 列表与协作

### 7.1 状态标记

Agent 列表与面板标题中的状态标记来自后台服务记录的**语义状态**，而不是进程是否存活：

| 标记 | 含义 |
| --- | --- |
| `WORK` | 明确的工作中（Working） |
| `WAIT` | 等待用户决定或输入（Blocked） |
| `DONE*` | 已完成，且该会话还有未读通知或未确认的收件箱消息 |
| `DONE` | 已完成，没有未读内容 |
| `IDLE` | 空闲（Idle），例如完成通知已被确认 |
| `EXIT 退出码` | 会话进程已退出 |
| `EXIT` | 语义状态为 Exited |
| `?` | 没有可靠的活动依据（Unknown） |

状态的来源、Claude Code 与 Codex 的回调、屏幕识别规则与 `agent-report` 见 [Agent 集成指南](agent-integration.md)。

### 7.2 排序

列表按关注优先级从高到低排列，同级按启动时间从早到晚：

| 优先级 | 条件 |
| --- | --- |
| 1 | 等待决定（Blocked） |
| 2 | 有未读的完成通知 |
| 3 | 有未确认的收件箱消息 |
| 4 | 工作中（Working） |
| 5 | 其他 |

列表重新排序时保持原来选中的会话（按会话标识，而不是行号）。在列表中按 Enter（或双击）把会话显示到终端区，
必要时先切换到会话工作目录所属的项目。

### 7.3 “selected” 指的是活动面板

命令面板中名称带 selected 的 Agent 命令，以及发消息、收件箱等命令，都作用于**活动面板中显示的会话**，而不是 Agent 列表中高亮的那一行。
先在列表中按 Enter 把目标会话显示到活动面板，再执行命令。

### 7.4 通知

会话进入 Done 或 Blocked 时生成一条未读通知；同一次状态变化有未读内容时，状态栏显示 `名称: 原因  ·  F9 → Agent: Notifications`。

- `Agent: Notifications`：列出全部通知，未读在前，其次等待决定的在前，再按时间从新到旧；每行为 `NEW` 或 `read`、会话名、状态与原因。
  选择一条即把对应会话显示到终端区并把这条通知标为已读。
- `Agent: Mark selected as read`：把活动面板会话的全部未读通知标为已读（不影响收件箱消息）。
- 确认一个完成通知后，若该会话没有其他未读的完成通知，状态从 Done 变为 Idle。确认 Blocked 通知后状态仍是 Blocked——
  已读不等于批准，审批要在该 Agent 的终端中明确回答。
- 只是查看列表或切换会话不会把任何通知标为已读。

### 7.5 解释状态

`Agent: Explain selected status` 显示活动面板会话的 `State`、`Source`、`Reason`、`Changed` 与 `Revision`，以及各标记的含义说明。

### 7.6 发送消息、交接与收件箱

| 命令 | 发件方 | 行为 |
| --- | --- | --- |
| `Agent: Send message` | 活动面板的会话 | 选择收件人（不含自己与已退出的会话），输入消息；消息只放入对方的收件箱，不在对方终端中输入任何文字。状态栏显示 `Message queued to 名称  ·  消息标识` |
| `Agent: Handoff prompt` | 活动面板的会话 | 消息存入对方收件箱，同时把文字作为提示输入对方终端并按 Enter 提交。状态栏显示 `Prompt delivered to 名称  ·  消息标识` |
| `Agent: Inbox` | — | 打开活动面板会话的收件箱：未读（`NEW`）在前，再按时间从新到旧；选择一条显示发件人、收件人、时间、消息标识、回复对象与全文，关闭后该消息标为已确认 |

- 交接要求目标会话仍在运行且不处于 Blocked；对方正在等待审批时会被拒绝，提示在其终端中明确回答。
  多行提示只在目标开启 bracketed paste 时投递（界面的输入框本身是单行的）。
- 消息与提示最多 32,768 个字符，不能包含换行与制表符以外的控制字符。
- 消息已保存但终端投递失败时，错误信息为 `Message was stored, but terminal delivery failed: …`，消息仍可在收件箱中查看，不要盲目重复提交。
- 投递成功只说明输入已经发送，不证明模型已经执行或完成任务。命令行等价操作与 `--reply-to` 回复见
  [CLI 使用说明](cli-guide.md#收件箱提示与任务交接)。

### 7.7 等待状态

`Agent: Wait for selected state` 让工作台在后台等待活动面板的会话进入某个状态：

1. 在 `WAIT FOR STATE` 中选择目标状态：`Done`、`Blocked`、`Idle`、`Working`、`Exited`；
2. 输入超时秒数（1–60，默认 30）。

等待期间状态栏显示 `Waiting for 名称…`，界面可以继续使用。目标状态出现时弹出 `WAIT RESULT · 名称`，显示状态、来源、原因与变化时间；
当前状态已经匹配时立即返回。超时或失败以消息框报告。同一个后台服务最多同时进行 8 个等待。

### 7.8 终止会话

`Agent: Terminate selected session` 经确认（`Stop this session and its child processes?`，按 Alt+O 确认，Enter 取消）后结束活动面板的会话
及其整个进程树。会话从 Agent 列表中消失，活动面板变为空面板（下一次刷新可能自动显示本项目中另一个未显示的会话）。
会话记录仍然保留，之后可以恢复原生对话或重新开始。进程自行退出的会话会以 `EXIT 退出码` 留在列表中，直到被终止或遗忘。

### 7.9 原生会话：恢复、重新开始、绑定与遗忘

后台服务为每个会话保存启动命令、工作目录、Provider 与原生会话标识。命令面板提供四个独立入口：

| 命令 | 列表内容 | 行为 |
| --- | --- | --- |
| `Agent: Resume original conversation` | 只剩记录或进程已退出的会话，显示 `名称  ·  Provider  ·  ready` 或不能恢复的原因 | 用记录的精确原生标识（或登记的恢复命令）恢复原对话；不能精确恢复时报错并提示绑定 |
| `Agent: Restart saved command as new conversation` | 同上，显示保存的命令 | 用保存的命令开始一个新对话，不继承原对话 |
| `Agent: Bind native conversation / resume command` | 全部会话记录 | 先输入 Provider（`claude / codex / gemini / opencode / copilot / custom`）；前五种要求输入确切的原生标识（不能用 latest 或部分前缀），其他 Provider 要求输入能恢复这一对话的完整命令 |
| `Agent: Forget dormant session record` | 只剩记录或进程已退出的会话 | 删除 CrewTerm 的记录，Provider 自己保存的对话历史不受影响 |

恢复与重新开始都沿用原会话标识，以活动面板的当前尺寸启动，显示到活动面板；必要时先切换到记录所属的项目。
各 Provider 的恢复规则与限制见 [原生会话](native-sessions.md)。

## 8. 文件区与编辑器

### 8.1 文件列表

文件列表显示当前项目中的一个目录，标题为 `FILES  ·  /`（根目录）或 `FILES  ·  相对路径`。

- 目录在前、按名称排序，目录名前显示 `▸`；不在根目录时第一行为 `../`。
- 不显示 `.git` 与符号链接等重解析点。
- 选中后按 Enter（或双击）：`../` 返回上级，目录则进入，文件则在编辑器中打开。
- 列表不会自动刷新：Agent 新建或删除文件后，用 `Files: Refresh directory` 重新读取当前目录。

### 8.2 打开文件

| 入口 | 说明 |
| --- | --- |
| 文件列表中按 Enter | 打开选中文件，光标在第 1 行 |
| `Files: Open path / file:line:column` | 输入相对或绝对路径，可带 `:行:列` 或 `(行,列)`，跳到对应行 |
| 终端右键 `Open selected file reference` | 见 [6.6](#66-从-fileline-跳转到代码) |
| `Files: Search project text` 的结果 | 见 [8.6](#86-全文搜索) |

只能打开当前项目内、不经过符号链接的文件；文件不超过 4 MiB，必须是有效的 UTF-8、UTF-16 或 UTF-32 文本且不含 NUL（二进制文件会被拒绝）。
打开前若当前文件有未保存修改，先询问保存、放弃或取消。

### 8.3 编辑

编辑器显示行号，按扩展名选择语法高亮，不自动换行，按 Tab 插入空格（有选区时缩进所选行，Shift+Tab 反缩进）。
常用按键（编辑器控件的默认绑定）：

| 按键 | 作用 |
| --- | --- |
| Ctrl+Z | 撤销 |
| Ctrl+Y 或 Ctrl+Shift+Z | 重做 |
| Ctrl+X / Ctrl+C / Ctrl+V | 剪切 / 复制（没有选区时复制当前行） / 粘贴 |
| Ctrl+A | 全选 |
| Shift+方向键、Shift+Home / End | 扩展选区 |
| Ctrl+← / Ctrl+→ | 按单词移动 |
| Ctrl+Home / Ctrl+End | 文档开头 / 结尾 |
| 右键 | 编辑菜单（撤销、重做、剪切、复制、粘贴、全选） |

撤销历史以打开或保存时的内容为基准：撤销回该点时标题前的 `*` 消失。关闭、切换或重新加载文件时撤销历史随之丢弃。

编辑器获得焦点时 F1–F10、Ctrl+Shift+P / V / C、Alt+1–5 仍是工作台快捷键：例如 F3 打开新建 Agent，Ctrl+Shift+C / V 作用于活动终端
而不是编辑器。编辑器内复制粘贴请用 Ctrl+C / Ctrl+V。

### 8.4 保存、冲突与重新加载

- **保存**：F5 或 `Editor: Save file`。保存沿用文件原来的编码（含 BOM）与换行风格，以“临时文件 → 原子替换”写盘，
  状态栏显示 `Saved 路径`。没有打开文件时提示 `No file is open. Select a file in FILES (F4) first.`。保存后内容超过 4 MiB 时拒绝保存。
- **冲突**：保存前和替换前都会比对磁盘内容。文件在打开之后被外部（例如 Agent）修改或删除时拒绝保存，提示
  `File changed outside the editor: 路径. Reload or compare before saving.`，编辑器内容与未保存状态保持不变，磁盘上的新内容也不会被覆盖。
- **重新加载**：`Editor: Reload file from disk` 从磁盘重新读取当前文件，光标回到第 1 行。有未保存修改时先询问：
  选 `Discard` 才会载入磁盘上的新内容；需要保留自己的修改时，先复制出来再重新加载。
- **未保存修改保护**：切换文件或项目、重新加载、分离、停止全部、捕获或恢复检查点之前，编辑器有未保存修改时都会弹出 `Unsaved changes`。

### 8.5 跳转行与查找

- `Editor: Go to line`：输入行号（默认 `1`），超出范围时跳到最近的有效行；输入不是整数时什么也不做。
- `Editor: Find text`：从光标处向后查找下一处（不区分大小写）并选中。再次查找下一处时重新执行该命令（F3 被工作台占用）；
  Shift+F3 按同一关键字向前查找上一处。编辑器内按 Ctrl+F 打开同一个查找框；Ctrl+H 不会打开替换框。

### 8.6 全文搜索

`Files: Search project text` 在当前项目中搜索字面文本（不区分大小写），在后台运行，期间界面可以继续使用。

- 最多 500 条结果，列表标题为 `SEARCH · N matches`，每行为 `路径:行号  该行内容`；选择一条即打开文件并跳到该行。
- 跳过 `.git`、`.vs`、`bin`、`obj`、`node_modules`、`artifacts`、`.crewterm` 目录和符号链接，
  以及无法编辑的文件（超过 4 MiB、二进制或非 Unicode 编码）。
- 搜索结束时如果已切换到其他项目，只显示结果而不打开文件。

### 8.7 把选中代码发给 Agent

`Editor: Send selected code to an agent` 把编辑器中选中的行（没有选区时为光标所在行）连同指令插入某个 Agent 的输入区：

1. 输入指令（提示为 `Lines 起始–结束: instruction`，默认 `Review this code`）；
2. 选择目标会话（只列出未退出的会话，不分项目）。

插入的文本格式如下，内容取自编辑器中的当前文本（可以是尚未保存的修改）：

```
Review this code
File: src/Parser.cs:40-52

（所选代码）
```

- 文本以 bracketed paste 方式进入与按键相同的输入队列，**不会自动提交**；状态栏提示
  `Code inserted into 名称. Review and press Enter in that terminal to submit.`，检查后在目标终端中自己按 Enter。
- 目标程序没有开启 bracketed paste 时拒绝发送，避免代码被当作 Shell 命令执行。
- 所选代码最多 60,000 个字符；代码中可能提前结束 bracketed paste 的转义字符会被去掉。

## 9. Git 与 worktree

按 **F6**（或 `Git: Changes / diff / stage / worktrees`）打开 `GIT` 菜单，对当前项目执行操作。需要安装 Git 并在 `PATH` 中。
所有 Git 调用都在后台执行，状态栏显示进度，失败以消息框报告；单条 Git 命令最长 60 秒。

| 菜单项 | 行为 |
| --- | --- |
| `Review working changes` | 列出项目目录下的全部变更（含未跟踪文件），每行为 Git 的两字符状态码加路径；选择一个文件显示工作区 diff（未跟踪文件显示为整个新文件） |
| `Review staged changes` | 只列出已暂存的文件，选择后显示暂存区 diff |
| `Stage a file` | 列出全部变更，暂存所选文件（`git add`），状态栏显示 `Staged 路径` |
| `Worktrees: list / open` | 列出仓库的全部 worktree（`分支  ·  路径`，分离 HEAD 显示为 `detached 短哈希`）；选择一个即加入项目列表并切换过去 |
| `Worktrees: create isolated branch` | 依次询问新分支名（默认 `crew/feature`）与检出目录（默认为项目目录加 `-worktree`）；分支已存在时检出该分支，否则从 HEAD 新建；创建后切换到新目录 |
| `Worktrees: remove clean checkout` | 列出 worktree，确认后移除所选检出目录；有未提交修改时 Git 会拒绝 |

变更列表只包含当前项目目录（可以是仓库的子目录）下的文件。多个 Agent 在同一目录中会看到彼此的修改，
需要隔离时为每个 Agent 创建一个 worktree，再在新项目中按 F3 启动 Agent。并行工作的完整流程见 [多 Agent 工作流实战](workflows.md)。

## 10. 后台任务与流水线

按 **F7**（或 `Tasks: Run / history / cancel / pipelines`）打开 `TASKS` 菜单。任务由后台服务持有，关闭输出窗口或分离工作台都不会结束任务。
任务的标准输入已关闭，适合构建、测试等无需交互的命令；需要输入或审批的程序请在终端会话中运行。

| 菜单项 | 行为 |
| --- | --- |
| `Run command` | 输入命令（默认 `dotnet test`），在当前项目根目录中作为后台任务运行，并打开输出窗口 |
| `Live tasks / history / cancel` | 列出运行中与已结束的任务（`RUNNING` / `EXIT 退出码` / `CANCELLED`、名称、开始时间），选择后打开输出窗口 |
| `Run saved command` | 选择命令模板，在**当前选中的项目**中运行（不是保存模板时的项目） |
| `Save command template` | 输入名称（默认 `Test`）与命令（默认 `dotnet test`）；同名（不区分大小写）的模板被替换 |
| `Run pipeline` | 选择流水线模板（显示步骤数），在当前项目中按顺序执行各步骤，遇到失败即停止 |
| `Create pipeline template` | 输入名称（默认 `Build and test`），再逐步输入命令（第一步默认 `dotnet build`），留空结束，最多 20 步；中途取消放弃整个模板 |
| `Remove command template` | 删除一个命令模板 |
| `Remove pipeline template` | 删除一个流水线模板 |

**输出窗口**（`TASK OUTPUT · 任务标识`）每 350 毫秒刷新一次：第一行为 `名称 · 状态 · 工作目录`，下面是输出（去掉颜色等转义序列）；
光标在末尾时自动跟随新输出。`Cancel task` 请求终止任务进程树（任务结束后变为不可用），`Close (keep running)` 或 Esc 只关闭窗口。

- 命令交给系统 Shell 执行：Windows 为 `cmd.exe`，Linux / macOS 为 `/bin/sh -c`。
- 每个任务保留输出的最后 262,144 个字符；已结束的任务保留最近 100 条，后台服务重启后仍可查看。
- 同时运行的任务数由 [设置](#13-设置) 中的并发上限控制（默认 4）。
- 命令模板保存在 `workspace.json`，流水线模板保存在 `pipeline-templates.json`；流水线模板不绑定项目。
  命令行的任务操作见 [CLI 使用说明](cli-guide.md#后台命令任务)。

## 11. 文件检查点

`Checkpoints: Capture / preview / restore` 打开 `CHECKPOINTS` 菜单，为当前项目的文件保存快照，并在需要时预览后恢复。
适合在让 Agent 大范围修改之前保留一份可回退的副本。

- **`Capture current project files`**：先处理编辑器中的未保存修改，再输入名称（默认 `Before agent changes`），在后台捕获；
  完成后状态栏显示 `Checkpoint saved · 文件数 files · 大小 KiB`。
- **`Preview / restore checkpoint`**：列出当前项目的检查点（`名称 · 时间 · 文件数`），选择后在后台比较并显示 `RESTORE PREVIEW · N changes`：
  每项为 `Added`（检查点之后新增，恢复时**删除**）、`Missing`（当前缺失，恢复时重新创建）或 `Modified`，并附当前内容与检查点内容的对照。
  有变更时，处理未保存修改并确认后在后台恢复。

| 规则 | 说明 |
| --- | --- |
| 范围 | 跳过 `.git`、`.vs`、`bin`、`obj`、`node_modules`、`artifacts`、`.crewterm` 目录与符号链接 |
| 上限 | 最多 4,000 个文件、合计 64 MiB，单个文件不超过 4 MiB；超出时捕获失败 |
| 冲突 | 预览之后文件又被修改时拒绝恢复；恢复逐个文件原子写入，不是跨文件的原子事务。恢复前请先让正在编辑这些文件的 Agent 停下 |
| 编辑器 | 恢复后若编辑器仍显示恢复前的同一文件且没有新修改，则自动重新加载；否则保留编辑器内容并提示手动重新加载 |
| 存储 | 保存在状态目录的 `checkpoints/` 下，不写入项目目录 |

## 12. 本地插件

本地插件是你登记的本机目录中的命令。命令面板中的管理入口：

| 命令 | 行为 |
| --- | --- |
| `Plugins: Install local folder` | 输入包含 `crewterm-plugin.json` 的目录（相对路径按启动工作台时的当前目录解析）；登记后**默认停用** |
| `Plugins: Enable / disable` | 列出插件（`ON` / `OFF`、名称、版本、源目录），选择即切换启用状态 |
| `Plugins: Inspect local manifest` | 显示源目录、启用状态与登记时保存的清单 JSON |
| `Plugins: Remove registration` | 移除登记，插件目录中的文件保持不变 |
| `Plugins: Execution history` | 列出执行记录（`OK` / `FAILED`、插件/命令、退出码），选择后显示完整结果 |

每个已启用插件的每条命令以 `Plugin: 插件名 / 命令名` 出现在命令面板中。运行在后台进行，完成后在 `PLUGIN OUTPUT · 插件标识` 中显示
命令、是否成功、退出码、是否超时 / 取消 / 截断、错误与输出。传给插件的数据包含当前项目根目录（`projectRoot`）、
活动面板的会话标识（`sessionId`）与状态目录（`stateDirectory`）。

插件以你的用户身份运行，没有沙箱，只安装你信任的插件。清单格式、事件、超时与输出上限见 [本地插件](local-plugins.md)。

## 13. 设置

`Settings: Concurrency and state directory` 修改后台任务的最大并发数（1–32，默认 4），保存后显示状态目录的位置。
新的并发数要在后台服务重启后才生效：保存工作后执行 `Workspace: Stop all sessions and background tasks`，再重新打开工作台。
其他配置项与状态目录中的文件见 [配置与数据参考](configuration.md)。

## 14. 分离、停止与重新连接

| 操作 | 入口 | 影响 |
| --- | --- | --- |
| 分离 | **F10**、`Workspace: Detach (keep all sessions running)`；焦点不在终端面板时按 Esc 也会请求退出 | 关闭界面；后台服务中的会话与任务继续运行 |
| 停止全部 | `Workspace: Stop all sessions and background tasks` | 确认（`Terminate ALL embedded terminals and cancel ALL running tasks?`）后结束全部终端会话、取消全部任务并关闭后台服务和界面 |
| 终止一个会话 | `Agent: Terminate selected session` | 见 [7.8](#78-终止会话) |
| 关闭面板 / 标签页 | 见 [第 5 节](#5-标签页与分屏) | 只改变布局 |

- 分离与停止之前都会先处理编辑器中的未保存修改并保存布局；任何一步选择取消，工作台继续运行。
- 分离时会用最多 5 秒把已接受的终端输入送完。仍有输入未送达时，界面退出后提示
  `CrewTerm: some input could not be delivered. Reconnect and inspect the terminal before retrying a command.` 并以退出码 1 结束。
- 再次运行 `crewterm`（使用相同的 `--state-dir`）即连接回原来的后台服务，回到上次的项目与布局。
- 后台服务重启或电脑重启后进程无法恢复，面板标题显示 `SAVED SESSION  ·  F9 Resume`，可用 `Agent: Resume original conversation`
  续接已记录的原生对话，或用 `Agent: Restart saved command as new conversation` 重新开始。

**旧版后台服务**：新版工作台不会自动停止或替换正在运行的旧版后台服务。连接旧版服务时，标题显示 `OLD SERVER`，状态栏与 F1
帮助显示警告；终端、任务等基本功能照常可用，通知、消息、等待、原生会话与插件等需要新协议的命令会被拒绝并显示升级说明，
命令面板中也不显示插件命令。保存旧终端中的工作后运行 `crewterm stop`，再重新打开工作台。

更多问题见 [常见问题](faq.md)，术语见 [术语表](glossary.md)。
