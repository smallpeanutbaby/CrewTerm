# CrewTerm 快捷键与命令参考

本文是工作台全部快捷键、各区域按键、命令面板命令与终端右键菜单的速查表，供日常查阅；各功能的完整说明见 [工作台使用手册](user-guide.md)。
命令名均与界面显示的文字一致，可以直接复制到命令面板中过滤。

## 目录

- [1. 全局快捷键](#1-全局快捷键)
  - [1.1 前缀键 Ctrl+b](#11-前缀键-ctrlb)
- [2. 各区域内的按键](#2-各区域内的按键)
  - [2.1 终端面板](#21-终端面板)
  - [2.2 标签栏与分隔线](#22-标签栏与分隔线)
  - [2.3 编辑器](#23-编辑器)
  - [2.4 侧栏](#24-侧栏)
  - [2.5 对话框、选择器与命令面板](#25-对话框选择器与命令面板)
- [3. 命令面板命令](#3-命令面板命令)
- [4. 菜单项：Git、任务与检查点](#4-菜单项git任务与检查点)
- [5. 终端右键菜单](#5-终端右键菜单)
- [6. 默认值与上限](#6-默认值与上限)

## 1. 全局快捷键

全局快捷键在主窗口中始终有效，与焦点在哪个区域无关（包括终端面板获得焦点时）；有对话框打开时不起作用。

| 按键 | 作用 | 等价命令 / 说明 |
| --- | --- | --- |
| F1 | 帮助窗口 `SHORTCUTS · CrewTerm` | `Help: Shortcuts and usage` |
| F2 | 添加项目（新空间） | `Project: Add folder`；也可点击 `spaces` 旁的 `+ new` |
| F3 | 新建 Agent 或终端 | `Agent: New embedded terminal`；需要当前项目；也可点击 `agents` 旁的 `+ new` |
| F4 | 显示 / 隐藏终端下方的代码查看器（默认隐藏，打开文件时自动显示） | 隐藏时焦点回到终端；显示时聚焦查看器，没有打开文件时聚焦侧栏 `files`；也可 `Ctrl+b e` |
| F5 | 保存当前文件 | `Editor: Save file` |
| F6 | Git 菜单 | `Git: Changes / diff / stage / worktrees` |
| F7 | 后台任务菜单 | `Tasks: Run / history / cancel / pipelines` |
| F8 | 切换分屏 | `Terminal: Toggle split` |
| F9 | 命令面板 | 也可右键单击标签栏 |
| F10 | 分离（会话与任务继续运行） | `Workspace: Detach (keep all sessions running)` |
| Ctrl+Shift+P | 命令面板 | 同 F9 |
| Ctrl+Shift+V | 把剪贴板粘贴到活动终端 | `Terminal: Paste clipboard` |
| Ctrl+Shift+C | 复制活动终端的选区，没有选区时复制可见屏幕 | `Terminal: Copy selected text` |
| Alt+1 | 聚焦当前标签页中的第一个终端面板 | 视觉顺序：从左到右、从上到下 |
| Alt+2 | 聚焦第二个终端面板 | 只有一个面板时无效 |
| Alt+3 | 聚焦代码查看器 | 查看器隐藏时先显示 |
| Alt+4 | 聚焦侧栏 `files` | 也可 `Ctrl+b f`；侧栏隐藏时先显示 |
| Alt+5 | 聚焦侧栏 `agents` | 也可 `Ctrl+b g` |
| Ctrl+b | 前缀键 | 之后再按一个键，见 [1.1](#11-前缀键-ctrlb) |

- 只有不带修饰键的 F1–F10 与 Ctrl+b 前缀是全局快捷键；Shift+F5、Ctrl+F2 等组合以及 F11、F12 在终端面板中发给子程序。
- Ctrl+Shift+V / Ctrl+Shift+C 始终作用于活动终端面板，即使焦点在编辑器中。
- Esc 不会退出工作台：终端面板获得焦点时发给子程序，在侧栏与代码查看器中把焦点还给终端。离开工作台用 F10 或 `Ctrl+b q`。
- **macOS**：Alt 组合键需要把 Option 键用作 Meta 键（Terminal.app 的“将 Option 键用作 Meta 键”，iTerm2 的 “Left Option key: Esc+”）；
  功能键可能需要同时按住 Fn。
- 部分宿主终端自己占用 Ctrl+Shift 组合键，这时按键到不了 CrewTerm，请改用 F9 或右键菜单中的对应命令。

### 1.1 前缀键 Ctrl+b

仿照 herdr / tmux：先按 `Ctrl+b`，状态栏列出可用的按键，再在 2 秒内按一个键（超时或单击鼠标即取消；单独的修饰键按下不计）。焦点在终端、侧栏或代码查看器中时都有效，
对话框打开时不起作用。

| 按键 | 作用 | 对应的命令或快捷键 |
| --- | --- | --- |
| `Ctrl+b` `N` | 添加项目（新空间） | F2 |
| `Ctrl+b` `c` | 新建标签页 | `Tab: New` |
| `Ctrl+b` `n` / `p` | 下一个 / 上一个标签页 | `Tab: Next` / `Tab: Previous` |
| `Ctrl+b` `1`–`9` | 跳到第几个标签页 | 点击标签 |
| `Ctrl+b` `T` | 重命名标签页 | `Tab: Rename` |
| `Ctrl+b` `X` | 关闭标签页（会话继续运行） | `Tab: Close (keep sessions running)` |
| `Ctrl+b` `v` / `-` | 向右 / 向下分屏 | `Terminal: Split right` / `Terminal: Split down` |
| `Ctrl+b` `o` | 聚焦下一个面板 | `Terminal: Focus next pane` |
| `Ctrl+b` `x` | 关闭面板（会话继续运行） | `Terminal: Close pane (keep session running)` |
| `Ctrl+b` `w` / `g` / `f` | 聚焦侧栏 `spaces` / `agents` / `files` | Alt+5（agents）、Alt+4（files） |
| `Ctrl+b` `e` | 显示或隐藏代码查看器 | F4 |
| `Ctrl+b` `b` | 显示或隐藏侧栏 | — |
| `Ctrl+b` `q` | 分离，会话与任务继续运行 | F10 |
| `Ctrl+b` `?` | 帮助 | F1 |
| `Ctrl+b` `Ctrl+b` | 把 `Ctrl+b` 本身发给获得焦点的终端 | — |
| `Ctrl+b` Esc | 取消 | — |

## 2. 各区域内的按键

### 2.1 终端面板

终端面板获得焦点时，除第 1 节的全局快捷键外，所有能编码的按键都按下表发给子程序：

| 按键 | 发给子程序的内容 |
| --- | --- |
| 可打印字符 | 字符本身 |
| Enter | CR（`0x0D`） |
| Backspace | DEL（`0x7F`） |
| Tab / Shift+Tab | HT（`0x09`）/ `CSI Z` |
| Esc | ESC（`0x1B`） |
| ↑ ↓ → ← Home End | `CSI A` `B` `C` `D` `H` `F`；子程序开启应用光标键模式（DECCKM）时为 `SS3 A` 等；带修饰键时为 `CSI 1;m X` |
| Insert / Delete / PageUp / PageDown | `CSI 2~` / `CSI 3~` / `CSI 5~` / `CSI 6~`；带修饰键时为 `CSI n;m~` |
| F1–F4 | `SS3 P` / `SS3 Q` / `SS3 R` / `SS3 S`（不带修饰的 F1–F4 被工作台占用，见下文） |
| F5–F12 | `CSI 15~` `17~` `18~` `19~` `20~` `21~` `23~` `24~`；带修饰键时为 `CSI n;m~` |
| Ctrl+字母 | 对应的控制字符（Ctrl+A 为 `0x01` … Ctrl+Z 为 `0x1A`），例如 Ctrl+C 为 `0x03` |
| Ctrl+Space | NUL（`0x00`） |
| Alt+按键 | 在该按键的编码前加 ESC，例如 Alt+B 为 `ESC b` |

- 修饰值 m = 1 + Shift(1) + Alt(2) + Ctrl(4)，与 xterm 一致。
- Enter、Esc、Tab、Backspace 与 F1–F4 不编码 Shift / Ctrl 修饰（Shift+Tab 除外），Alt 仍会前缀 ESC。
- 被工作台占用的 F1–F10 用 `Terminal: Send reserved key (F1–F12 / Tab / Esc)` 发给子程序；该命令也能发送 F11、F12、Tab 与 Esc。
- 键入任何内容都会结束历史浏览并清除选区，面板回到最新画面。

鼠标：子程序开启鼠标跟踪（DECSET 1000 / 1002 / 1003）时事件转发给子程序；子程序没有开启跟踪、按住 Shift、或面板正在浏览历史时，在本地处理：

| 本地鼠标操作 | 效果 |
| --- | --- |
| 按下任一鼠标键 | 聚焦该面板 |
| 左键拖动 | 选择文本，松开时自动复制到剪贴板；选择期间画面冻结 |
| 左键单击 | 清除选区 |
| 滚轮 | 每格滚动 3 行历史；水平滚轮被忽略 |
| 右键 | `TERMINAL ACTIONS` 菜单（见 [第 5 节](#5-终端右键菜单)） |
| Shift+拖动 / Shift+右键 / Shift+滚轮 | 子程序跟踪鼠标时仍在本地选择、打开菜单、滚动历史 |

### 2.2 标签栏与分隔线

| 操作 | 效果 |
| --- | --- |
| 左键点击标签 | 切换到该标签页（活动标签高亮显示） |
| 左键点击 `+` | 新建标签页（`Tab: New`） |
| 右键单击标签栏 | 打开命令面板 |
| 拖动面板之间的分隔线（`│` 或 `─`） | 调整两侧比例（0.1–0.9），随布局保存 |

### 2.3 编辑器

下表是编辑器控件的默认按键，工作台没有修改它们；第 1 节的全局快捷键优先于编辑器。

| 按键 | 作用 |
| --- | --- |
| 方向键、Home / End、PageUp / PageDown | 移动光标 |
| Ctrl+← / Ctrl+→ | 按单词移动 |
| Ctrl+Home / Ctrl+End | 文档开头 / 结尾 |
| Shift+移动键 | 扩展选区 |
| Ctrl+A | 全选 |
| Ctrl+X / Ctrl+C / Ctrl+V | 剪切 / 复制（没有选区时复制当前行）/ 粘贴 |
| Ctrl+Z | 撤销 |
| Ctrl+Y、Ctrl+Shift+Z | 重做 |
| Tab / Shift+Tab | 缩进 / 反缩进（Tab 插入空格；有选区时作用于所选行） |
| Shift+F3 | 按 `Editor: Find text` 上次的关键字向前查找 |
| 右键 | 编辑菜单：撤销、重做、剪切、复制、粘贴、全选 |
| 滚轮 | 滚动 |

- F3 被工作台占用（新建 Agent），查找下一处请再次执行 `Editor: Find text`。
- Ctrl+F 与 `Editor: Find text` 打开同一个查找框；Ctrl+H 不会打开替换框；跳转行用 `Editor: Go to line`。
- F5 保存；Esc 把焦点还给终端，查看器保持显示（多光标编辑时 Esc 先收回为单个光标）；F4 隐藏查看器。

### 2.4 侧栏

侧栏 `spaces`、`agents` 与 `files` 三个分区的通用按键：

| 按键 / 鼠标 | 作用 |
| --- | --- |
| ↑ / ↓ | 上一项 / 下一项 |
| PageUp / PageDown、Home / End | 翻页 / 第一项、最后一项 |
| 输入一个字符 | 跳到下一个名称以它开头的行（不区分大小写，只选中不执行） |
| 单击、Enter | 选中并立即执行（见下表）；双击只执行一次 |
| 右键单击、Shift+F10 | 打开该行的菜单（`spaces` 与 `agents`） |
| Esc | 把焦点还给活动终端 |
| `+`、单击标题旁的 `+ new` | 添加项目 / 新建 Agent（`files` 没有 `+ new`） |
| 滚轮 | 滚动列表 |

| 分区 | 聚焦 | 单击或 Enter 的作用 |
| --- | --- | --- |
| `spaces` | 单击、`Ctrl+b w` | 切换到选中的项目；右键菜单：`Open`、`Start an agent here`、`Remove from sidebar` |
| `agents` | 单击、Alt+5、`Ctrl+b g` | 把选中的会话显示到终端区（已显示则聚焦所在面板）；右键菜单见 [使用手册 7.4](user-guide.md#74-agent-行的右键菜单) |
| `files  /目录` | 单击、Alt+4、`Ctrl+b f` | `‹ ..` 返回上级；目录则进入；文件则在终端下方的代码查看器中打开；列表每 2 秒自动重新读取 |

### 2.5 对话框、选择器与命令面板

| 对话框 | 按键 |
| --- | --- |
| 输入框 | Enter 确认（`OK`）；Esc 取消；Ctrl+A 全选输入框中的文字 |
| 选择器（含命令面板） | 直接输入文字过滤（不区分大小写的子串匹配）；↓ 从过滤框进入列表；Enter 或 `Select` 选择；Esc 或 `Cancel` 取消 |
| 确认框（`Confirm` / `Cancel`） | Alt+O 或点击 `Confirm` 确认；**Enter 选择默认按钮 `Cancel`**；Esc 或 Alt+C 取消 |
| `Unsaved changes`（`Save` / `Discard` / `Cancel`） | Alt+S 保存、Alt+D 放弃、Alt+C 取消；Enter 选择默认按钮 `Cancel`；Esc 取消 |
| 文本查看器（帮助、diff、历史、输出） | 方向键、PageUp / PageDown、Ctrl+Home / Ctrl+End 移动；Shift+方向键选择；Ctrl+C 复制；Ctrl+F 查找；Esc 或 `Close` 关闭 |
| 任务输出 `TASK OUTPUT` | `Cancel task` 取消任务；`Close (keep running)` 或 Esc 只关闭窗口 |
| 错误消息框 `CrewTerm` | Enter 或 `OK` 关闭 |

命令面板的过滤只匹配显示的命令名。对话框打开期间全局快捷键不起作用。

## 3. 命令面板命令

命令面板（F9、Ctrl+Shift+P 或右键单击标签栏）中的内置命令按下表顺序排列，之后是已启用插件提供的命令。
“活动面板”指获得焦点或最近选中的终端面板；名称中的 selected 指活动面板中显示的会话，而不是侧栏 `agents` 中高亮的行。

### 3.1 Project

| 命令 | 作用 | 前提条件 | 快捷键 / 其他入口 |
| --- | --- | --- | --- |
| `Project: Add folder` | 输入已有文件夹路径，加入项目列表并切换过去；已登记的直接切换 | 文件夹存在 | F2 |
| `Project: Remove from sidebar` | 从侧栏移除一个项目登记；文件夹与其中的会话不受影响 | — | — |

### 3.2 Agent

| 命令 | 作用 | 前提条件 | 快捷键 / 其他入口 |
| --- | --- | --- | --- |
| `Agent: New embedded terminal` | 在当前项目根目录启动一个 Agent CLI、Shell 或自定义命令，显示到活动面板 | 当前项目；所选 Agent 已安装 | F3；右键 `Start new Agent or shell` |
| `Agent: Save custom CLI / SSH profile` | 保存自定义配置：显示名、可执行文件名或完整路径（默认 `ssh`）、可选参数 | — | — |
| `Agent: Remove custom profile` | 删除一个自定义配置（内置配置不在列表中） | — | — |
| `Agent: Resume original conversation` | 从只剩记录或已退出的会话中选择一个，按记录的精确原生标识（或登记的恢复命令）恢复原对话 | 新版后台服务；记录可以精确恢复（列表显示 `ready` 或原因） | —（面板标题 `SAVED SESSION  ·  F9 Resume` 指向此命令） |
| `Agent: Restart saved command as new conversation` | 用保存的命令开始一个新对话，不继承原对话 | 新版后台服务 | — |
| `Agent: Bind native conversation / resume command` | 为会话记录登记 Provider 与原生标识（claude / codex / gemini / opencode / copilot）或显式恢复命令（其他 Provider） | 新版后台服务 | — |
| `Agent: Forget dormant session record` | 删除只剩记录或已退出会话的 CrewTerm 记录，Provider 历史保留 | 新版后台服务 | — |
| `Agent: Notifications` | 列出通知（未读、等待决定的在前）；选择一条即显示对应会话并标为已读 | 新版后台服务 | —（状态栏提醒 `F9 → Agent: Notifications` 指向此命令） |
| `Agent: Mark selected as read` | 把活动面板会话的全部未读通知标为已读 | 活动面板有会话；新版后台服务 | — |
| `Agent: Explain selected status` | 显示活动面板会话的状态、来源、原因、变化时间、修订号与标记说明 | 活动面板有会话；新版后台服务 | — |
| `Agent: Send message` | 从活动面板会话向另一个会话的收件箱发送消息，不在对方终端中输入 | 活动面板有会话；存在其他未退出的会话；新版后台服务 | — |
| `Agent: Handoff prompt` | 保存消息并把文字作为提示输入目标终端、按 Enter 提交 | 同上；目标未处于 Blocked | — |
| `Agent: Inbox` | 打开活动面板会话的收件箱，选择一条查看全文，关闭后确认 | 活动面板有会话；新版后台服务 | — |
| `Agent: Wait for selected state` | 选择状态（Done / Blocked / Idle / Working / Exited）与超时（1–60 秒，默认 30），后台等待活动面板会话进入该状态 | 活动面板有会话；新版后台服务 | — |
| `Agent: Terminate selected session` | 确认后结束活动面板的会话及其整个进程树，面板变为空面板 | 活动面板有会话 | — |
| `Agent: Choose second split terminal` | 选择一个会话显示在第二个面板；已显示则只聚焦；只有一个面板时先向右拆分 | — | — |

### 3.3 Terminal

| 命令 | 作用 | 前提条件 | 快捷键 / 其他入口 |
| --- | --- | --- | --- |
| `Terminal: Toggle split` | 只有一个面板时向右拆分；已有多个面板时收回为第一个面板（其余会话继续运行） | — | F8 |
| `Terminal: Split right` | 向右拆分活动面板，新面板获得焦点 | 当前标签页少于 16 个面板 | 右键 `Split right` |
| `Terminal: Split left` | 向左拆分活动面板 | 同上 | 右键 `Split left` |
| `Terminal: Split down` | 向下拆分活动面板 | 同上 | 右键 `Split down` |
| `Terminal: Split up` | 向上拆分活动面板 | 同上 | 右键 `Split up` |
| `Terminal: Close pane (keep session running)` | 从布局中移除活动面板，会话继续运行；唯一的面板关闭时标签页一起关闭 | — | 右键 `Close pane (keep session running)` |
| `Terminal: Focus next pane` | 按视觉顺序聚焦下一个面板，最后一个之后回到第一个 | — | — |
| `Terminal: Choose session for active pane` | 从全部会话中选择，显示到活动面板（已显示则聚焦所在面板） | — | 右键 `Choose existing session` |
| `Terminal: View / search scrollback` | 在只读查看器中显示活动会话的滚动历史与当前屏幕 | 活动面板有会话 | 右键 `View / search history` |
| `Terminal: Copy visible screen` | 复制活动会话当前可见屏幕的纯文本 | 活动面板有会话；剪贴板可用 | 右键 `Copy visible screen` |
| `Terminal: Copy selected text` | 复制活动面板的选区；没有选区时复制可见屏幕 | — | Ctrl+Shift+C；右键 `Copy selected text` |
| `Terminal: Paste clipboard` | 把剪贴板粘贴到活动会话（去掉 ESC 与 C1 控制字符；未开启 bracketed paste 时多行内容需确认） | 活动面板有会话；不超过 60,000 个字符 | Ctrl+Shift+V；右键 `Paste clipboard` |
| `Terminal: Send reserved key (F1–F12 / Tab / Esc)` | 从列表中选择 F1–F12、Tab 或 Esc，发给活动会话 | 活动面板有会话 | — |

### 3.4 Tab

| 命令 | 作用 | 前提条件 | 快捷键 / 其他入口 |
| --- | --- | --- | --- |
| `Tab: New` | 询问名称（默认 `Terminal N`）并新建一个只有空面板的标签页 | 当前项目少于 16 个标签页 | 标签栏 `+`、`Ctrl+b c` |
| `Tab: Rename` | 重命名活动标签页 | 名称为 1–60 个可打印字符 | — |
| `Tab: Close (keep sessions running)` | 关闭活动标签页，其中的会话继续运行；关闭最后一个时自动新建 `Terminal` 标签页 | — | — |
| `Tab: Next` | 切换到下一个标签页（循环） | — | 点击标签 |
| `Tab: Previous` | 切换到上一个标签页（循环） | — | 点击标签 |
| `Tab: Choose` | 从列表（`名称  ·  N panes`）中选择标签页 | — | 点击标签 |

### 3.5 Files 与 Editor

| 命令 | 作用 | 前提条件 | 快捷键 / 其他入口 |
| --- | --- | --- | --- |
| `Files: Open path / file:line:column` | 输入相对或绝对路径打开文件，可带 `:行:列` 或 `(行,列)` 跳到对应行 | 当前项目；文件在项目内 | 右键 `Open selected file reference`（用选中文本） |
| `Files: Search project text` | 在当前项目中搜索字面文本（不区分大小写，最多 500 条），选择结果即打开并跳到该行 | 当前项目 | — |
| `Files: Refresh directory` | 重新读取文件列表的当前目录 | — | — |
| `Editor: Save file` | 保存当前文件；磁盘内容已被外部修改时拒绝覆盖 | 已打开文件 | F5 |
| `Editor: Reload file from disk` | 从磁盘重新读取当前文件（有未保存修改时先询问），光标回到第 1 行 | 已打开文件 | — |
| `Editor: Go to line` | 输入行号（默认 `1`）跳转 | — | — |
| `Editor: Find text` | 从光标处向后查找下一处（不区分大小写）并选中 | — | Shift+F3 向前查找上一处 |
| `Editor: Send selected code to an agent` | 把选中的行（或光标所在行）连同指令以 bracketed paste 插入目标 Agent 的输入区，不自动提交 | 已打开文件；目标未退出且开启了 bracketed paste；不超过 60,000 个字符 | — |

### 3.6 Git、Tasks 与 Checkpoints

| 命令 | 作用 | 前提条件 | 快捷键 / 其他入口 |
| --- | --- | --- | --- |
| `Git: Changes / diff / stage / worktrees` | 打开 `GIT` 菜单（见 [4.1](#41-git-菜单f6)） | 当前项目；已安装 Git | F6 |
| `Tasks: Run / history / cancel / pipelines` | 打开 `TASKS` 菜单（见 [4.2](#42-任务菜单f7)） | 运行类菜单项需要当前项目 | F7 |
| `Checkpoints: Capture / preview / restore` | 打开 `CHECKPOINTS` 菜单（见 [4.3](#43-检查点菜单)） | 当前项目 | — |

### 3.7 Plugins

| 命令 | 作用 | 前提条件 | 快捷键 / 其他入口 |
| --- | --- | --- | --- |
| `Plugins: Install local folder` | 登记一个包含 `crewterm-plugin.json` 的本地目录，登记后默认停用 | 新版后台服务 | — |
| `Plugins: Enable / disable` | 列出插件（`ON` / `OFF`），选择即切换启用状态 | 新版后台服务 | — |
| `Plugins: Inspect local manifest` | 显示插件源目录、启用状态与登记时保存的清单 | 新版后台服务 | — |
| `Plugins: Remove registration` | 移除插件登记，源目录中的文件保持不变 | 新版后台服务 | — |
| `Plugins: Execution history` | 列出插件执行记录（`OK` / `FAILED`、插件/命令、退出码），选择后显示完整结果 | 新版后台服务 | — |
| `Plugin: 插件名 / 命令名` | 每个已启用插件的每条命令各一项；在后台运行，完成后显示 `PLUGIN OUTPUT` | 插件已启用；新版后台服务 | — |

插件命令收到的数据包含 `projectRoot`（当前项目，可能为空）、`sessionId`（活动面板的会话，可能为空）与 `stateDirectory`。
详见 [本地插件](local-plugins.md)。

### 3.8 Settings、Help 与 Workspace

| 命令 | 作用 | 前提条件 | 快捷键 / 其他入口 |
| --- | --- | --- | --- |
| `Settings: Concurrency and state directory` | 修改后台任务最大并发数（1–32），保存后显示状态目录；后台服务重启后生效 | — | — |
| `Help: Shortcuts and usage` | 显示帮助窗口 | — | F1 |
| `Workspace: Detach (keep all sessions running)` | 关闭界面，后台服务中的会话与任务继续运行 | — | F10；非终端焦点下的 Esc |
| `Workspace: Stop all sessions and background tasks` | 确认后结束全部终端会话与后台任务、停止后台服务并关闭界面 | — | — |

### 3.9 连接旧版后台服务时

界面显示 `OLD SERVER` 时，需要新协议的命令在发送前就被拒绝并显示升级说明，插件命令也不出现在命令面板中：

| 仍可使用 | 会被拒绝 |
| --- | --- |
| `Agent: New embedded terminal`、`Agent: Terminate selected session`、`Agent: Save custom CLI / SSH profile`、`Agent: Remove custom profile`、`Agent: Choose second split terminal`；全部 `Terminal:`、`Tab:`、`Files:`、`Editor:` 命令；Git、Tasks、Checkpoints；Settings、Help、Workspace | `Agent: Resume original conversation`、`Agent: Restart saved command as new conversation`、`Agent: Bind native conversation / resume command`、`Agent: Forget dormant session record`、`Agent: Notifications`、`Agent: Mark selected as read`、`Agent: Explain selected status`、`Agent: Send message`、`Agent: Handoff prompt`、`Agent: Inbox`、`Agent: Wait for selected state`；全部 `Plugins:` 命令 |

升级方法见 [工作台使用手册](user-guide.md#14-分离停止与重新连接)。

## 4. 菜单项：Git、任务与检查点

以下菜单项不在命令面板中，而是在对应菜单打开后选择。

### 4.1 Git 菜单（F6）

| 菜单项 | 作用 |
| --- | --- |
| `Review working changes` | 列出项目目录下的全部变更（Git 两字符状态码加路径，含未跟踪文件），选择后显示工作区 diff |
| `Review staged changes` | 只列出已暂存的文件，选择后显示暂存区 diff |
| `Stage a file` | 列出全部变更，暂存所选文件 |
| `Worktrees: list / open` | 列出 worktree（`分支  ·  路径` 或 `detached 短哈希  ·  路径`），选择后加入项目列表并切换过去 |
| `Worktrees: create isolated branch` | 输入分支名（默认 `crew/feature`）与检出目录（默认 `项目目录-worktree`），创建 worktree 并切换过去；分支已存在时检出该分支 |
| `Worktrees: remove clean checkout` | 选择并确认后移除 worktree 检出目录；有未提交修改时 Git 拒绝 |

### 4.2 任务菜单（F7）

| 菜单项 | 作用 |
| --- | --- |
| `Run command` | 输入命令（默认 `dotnet test`），在当前项目根目录作为后台任务运行并打开输出窗口 |
| `Live tasks / history / cancel` | 列出任务（`RUNNING` / `EXIT 退出码` / `CANCELLED`），选择后打开输出窗口，可在其中取消 |
| `Run saved command` | 选择命令模板，在当前选中的项目中运行 |
| `Save command template` | 保存命令模板：名称（默认 `Test`）与命令（默认 `dotnet test`），同名替换 |
| `Run pipeline` | 选择流水线模板，在当前项目中按顺序运行，遇到失败即停止 |
| `Create pipeline template` | 名称（默认 `Build and test`）加最多 20 个步骤（第一步默认 `dotnet build`），留空结束 |
| `Remove command template` | 删除一个命令模板 |
| `Remove pipeline template` | 删除一个流水线模板 |

### 4.3 检查点菜单

| 菜单项 | 作用 |
| --- | --- |
| `Capture current project files` | 输入名称（默认 `Before agent changes`），捕获当前项目文件 |
| `Preview / restore checkpoint` | 选择检查点，预览 `Added` / `Missing` / `Modified` 变更，确认后恢复 |

## 5. 终端右键菜单

在终端面板上右键（子程序跟踪鼠标时用 Shift+右键）打开 `TERMINAL ACTIONS`，菜单项按以下顺序排列，都作用于被点击的面板：

| 菜单项 | 作用 | 等价命令 / 快捷键 |
| --- | --- | --- |
| `Copy selected text` | 复制选区；没有选区时复制可见屏幕 | `Terminal: Copy selected text`、Ctrl+Shift+C |
| `Copy visible screen` | 复制可见屏幕的纯文本 | `Terminal: Copy visible screen` |
| `Paste clipboard` | 粘贴剪贴板 | `Terminal: Paste clipboard`、Ctrl+Shift+V |
| `View / search history` | 打开滚动历史查看器 | `Terminal: View / search scrollback` |
| `Choose existing session` | 为该面板选择一个已有会话 | `Terminal: Choose session for active pane` |
| `Start new Agent or shell` | 新建 Agent 或终端 | `Agent: New embedded terminal`、F3 |
| `Split right` | 向右拆分 | `Terminal: Split right` |
| `Split down` | 向下拆分 | `Terminal: Split down` |
| `Split left` | 向左拆分 | `Terminal: Split left` |
| `Split up` | 向上拆分 | `Terminal: Split up` |
| `Open selected file reference` | 解析选中文本中的 `路径:行[:列]` 或 `路径(行[,列])` 并在编辑器中打开；没有行号时按路径打开 | `Files: Open path / file:line:column`（手动输入） |
| `Close pane (keep session running)` | 关闭该面板，会话继续运行 | `Terminal: Close pane (keep session running)` |

## 6. 默认值与上限

| 项目 | 值 |
| --- | --- |
| 界面刷新间隔 | 160 毫秒；任务输出窗口 350 毫秒 |
| 每个后台服务的会话数 | 最多 32 |
| 终端尺寸 | 2–300 列、2–120 行 |
| 每个项目的标签页 / 每个标签页的面板 | 最多 16 / 最多 16 |
| 标签页名称 | 1–60 个可打印字符，新建时默认 `Terminal N` |
| 分隔线比例 | 0.1–0.9，新拆分为 0.5 |
| 滚动历史 | 每个会话 2,000 行纯文本；本地滚轮每格 3 行 |
| 粘贴 / 代码上下文 | 最多 60,000 个字符 |
| 终端输入队列 | 2048 条 |
| 消息与提示 | 最多 32,768 个字符 |
| 等待状态 | 超时 1–60 秒，默认 30；每个后台服务同时最多 8 个等待 |
| 编辑文件 | 最大 4 MiB，UTF-8 / UTF-16 / UTF-32 文本 |
| 全文搜索 | 最多 500 条结果 |
| 检查点 | 最多 4,000 个文件、合计 64 MiB |
| Git 命令 | 单条最长 60 秒 |
| 后台任务 | 输出保留最后 262,144 个字符；历史保留 100 条；并发 1–32，默认 4 |
| 流水线模板 | 最多 20 步 |
| F3 自定义命令 | Windows 默认 `cmd.exe`；Linux / macOS 默认 `$SHELL`，未设置时 `/bin/sh` |
| 内置 Shell 配置 | Windows 为 `pwsh`，找不到时为 Windows PowerShell；Linux / macOS 为 `$SHELL` 或 `/bin/sh` |
| 后台任务的 Shell | Windows 为 `cmd.exe`；Linux / macOS 为 `/bin/sh -c` |

命令行的对应操作见 [CLI 使用说明](cli-guide.md)，状态目录与配置文件见 [配置与数据参考](configuration.md)。
