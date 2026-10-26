# v0.2 布局验证记录

## RED / GREEN

1. `dotnet test tests/CrewTerm.Core.Tests --filter FullyQualifiedName~TerminalLayoutTests --no-restore`
   - RED：`ProjectTabsAndPaneBindingsSurviveSettingsRoundTrip`，旧版保存 workspace.json 丢弃 ProjectLayouts。
   - GREEN：保留序列化布局后 1/1。
2. 同命令，新增树编辑、四向分割、关闭归并、比例、边界测试。
   - 首次缺少 API，随后只补声明观察 14 个 NotImplementedException 的运行期 RED；完成实现后 15/15 GREEN。
   - `dotnet test tests/CrewTerm.Core.Tests -c Release --no-restore`：36/36（包含目录中同期的其他 Core 回归测试）。
3. `dotnet test tests/CrewTerm.App.Tests -c Release --filter FullyQualifiedName~LayoutTuiTests --no-restore`
   - RED：真实 ConPTY 中不存在 `Terminal: Split right` 操作，停在命令面板。
   - 实现过程中实际发现 View 父容器 CanFocus=false 阻断输入、单行 Button 默认阴影令文字 viewport 高度为 0。
   - 为这两个问题新增 `TerminalLayoutViewTests`，观察预期失败；焦点容器改为可聚焦，标签改用专用可点击 Label。测试不再限定必须用 Button，而验证所有标签控件都有可画文字行。
   - GREEN：3/3（含真实进程独立输入、命名标签、三面板双轴嵌套、关闭布局不关闭进程、detach/reopen）。
4. `dotnet test tests/CrewTerm.App.Tests -c Release --filter FullyQualifiedName~TerminalLayoutViewTests --no-restore`
   - RED：真实 NewMouseEvent 拖动横/纵 divider，ratio 仍 0.5，期望 0.75。
   - GREEN：新增鼠标抓取和基于父区域的比例计算后 5/5；也检查点击标签选择正确原会话。
5. `dotnet test tests/CrewTerm.App.Tests -c Release --filter 'FullyQualifiedName~LayoutTuiTests|FullyQualifiedName~TuiSmokeTests' --no-restore`
   - 8/8 GREEN，26 秒。新增真实外层 ConPTY SGR 鼠标拖拽、点击标签及比例保存验证；原来 7 项 UI smoke 全部保留通过。

## UI 联调回归

以下均先运行新增失败断言，再修改生产实现；实际终端测试使用独立临时状态目录，不连接用户的默认后台服务。

| RED / GREEN 命令过滤器（均使用 `dotnet test tests/CrewTerm.App.Tests -c Release --no-restore --filter ...`） | 实际 RED 原因 | GREEN |
|---|---|---|
| `FullyQualifiedName~LeavingFocusedSplit` | 重建标签/项目时旧 pane 的失焦回调改变新布局选择，查找当前 pane 抛异常 | 捕获所属布局/标签，重建期间忽略旧回调；两种切换均通过，布局 View 全部 7 项通过 |
| `FullyQualifiedName~PriorityRefreshPreserves` | Agent 优先级重排后，Enter 选中了旧索引对应的另一个 Agent；输入未到达 Second Agent | 刷新数组前保存选中会话 ID，重排后按 ID 选择；真实输入验证通过 |
| `FullyQualifiedName~InitialTerminalReceives` | 初次打开的终端没有键盘焦点，输入未进入已绑定会话 | 在首次有效布局完成后聚焦；无需额外 Alt+1 即可输入 |
| `FullyQualifiedName~TypingAfterScroll` | 屏幕滚动到历史后输入已送达子进程，但画面仍停在历史 | 全局输入分发也调用 ScrollToBottom，通过。测试先补上明确聚焦以隔离此问题，保留原输出断言 |
| `FullyQualifiedName~MouseTabRefreshRegressionTests` | 鼠标切换标签后，旧轮询 generation 仍被接受 | 选择回调推进刷新代次，旧画面不能写回已释放 pane；真实 View 鼠标事件通过 |
| `FullyQualifiedName~RightClickMenu` | 子终端收到右键，但工作台没有菜单，不能从鼠标入口分屏 | 连接 TerminalPane 回调到实际窗口菜单；真实 ConPTY 右键→Split down→2 panes 通过 |
| `FullyQualifiedName~NotificationsPrioritize` | 原界面没有语义状态/通知入口 | WAIT/DONE* 排序、打开一条通知只确认对应记录、显式已读转 Idle，真实 TUI 通过 |
| `FullyQualifiedName~CooperationMenus` | 没有收件箱/交接入口；后续新增等待操作时命令面板找不到 Wait for selected state | 实际定向消息、查看并确认回复、向另一个 cmd 发送真实输入、后台等到 Done 结果，全部通过 |
| `FullyQualifiedName~ExtensionTuiTests` | 缺少原生恢复与本地插件 UI 操作 | 重启后的 exact UUID 参数收到；安装默认停用→启用→执行真实 pwsh 插件写 proof.json→移除注册，2 项通过 |
| `FullyQualifiedName~LegacyServerTuiTests` | 旧协议后台连接没有明显版本说明 | 保持 OLD SERVER 标题，F1 说明用户完成工作后显式停止再升级；未发出 stop，通过 |

原生恢复 TUI fixture 最初使用任意脚本名；原生适配器新增严格校验后，将 fixture 更名为 `codex.cmd` 以模拟真实 provider 入口。未知 wrapper 保持拒绝自动拼接续接参数，未放宽生产校验。

联合等待/旧服务验收：`dotnet test tests/CrewTerm.App.Tests -c Release --filter 'FullyQualifiedName~CooperationMenus|FullyQualifiedName~LegacyServerTuiTests' --no-restore`，实际 RED 为 2 失败、0 通过（缺少等待命令及版本提示）；实现后 2 通过、0 失败、0 跳过。

旧服务操作补充回归：同一 `LegacyServerTuiTests` 继续验证点击新通知功能时给出升级说明、且不向旧服务发送 `notifications`。先修正测试等待条件以区分底部状态文字与真正打开的 F1 帮助，随后观察实际 RED：弹窗为 `Unknown operation: notifications`。UI 请求增加版本检查后，同命令 1/1 GREEN；`stop` 从未发送。

## 环境说明

Debug App 被用户实际默认状态目录服务占用（当时 PID 18160），构建报 MSB3027。未停止该服务；改用 Release 进行根任务 UI 测试，各协作者使用自己的配置名。该构建失败不是功能 RED。

目录中同期出现了非本轮协作者编写的基础库重构/XML 注释改动，全部保留；本文件仅记录本轮布局工作的测试过程。
