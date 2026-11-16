# 任务面板回归

先运行 Windows 桌面测试，生成真实手机状态快照：

```powershell
dotnet run --project tests/FloorRegression -c Release -- --tasks-only C:\temp\task-checks
```

安装测试工具 `npm install --no-save playwright`，或将环境变量 `PLAYWRIGHT_MODULE` 指向已有安装。用已安装的 Edge/Chrome 时设置 `BROWSER_PATH`；否则先执行 `npx playwright install chromium`。

```powershell
node tests/mobile/task-filters.cjs C:\temp\task-checks
```

主线专项：先运行 `dotnet run --project tests/FloorRegression -c Release -- --story-only C:\temp\story-checks`，再运行 `node tests/mobile/story-tasks.cjs C:\temp\story-checks`。
验证默认隐藏、手动开启、两端同步、系列切换、关闭移除、日志状态隔离、橙红色绘制与无底图/无坐标处理。

桌面测试验证实际筛选谓词、旧缓存补全、未知值、持久化、选中和跨图跳转。浏览器测试使用桌面导出的选项与任务快照，验证两台客户端同步、异步动作回执、IME、防止轮询重置编辑、标签更新、独立地图状态、空态和 320/360px 布局。浏览器内的模拟服务仅转发筛选字段，不重复实现任务筛选谓词。
