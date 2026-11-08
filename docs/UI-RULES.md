# 界面与交互规则（踩过的坑）

这里是**给以后的我**看的：每条都是实际做错过、被用户指出来的。新功能涉及同类控件时先读一遍。

## 菜单 / 复选菜单

1. **`ToolStripMenuItem.CheckOnClick` 会顺手关掉菜单。**
   多选菜单里不要用它。改成在 `Click` 事件里自己切 `CheckState`，
   并取消菜单的关闭原因：

   ```csharp
   menu.Closing += (_, e) =>
   {
       if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
   };
   ```

2. **带子菜单的父项，点文字只展开子菜单，`Click` 不会触发。**
   所以"点父项全选/全不选"做不到。解决办法：在子菜单顶部放
   `全部显示` / `全部隐藏` 两条独立命令。

3. **程序里改 `CheckState` 后必须手动重画**，否则要鼠标扫过那一项才更新：

   ```csharp
   item.CheckState = CheckState.Unchecked;
   parent.DropDown.Invalidate(true);   // 少这句就是"点了没反应，扫过去才变"
   ```

4. 父项展示子项汇总用 `CheckState.Indeterminate`；`ToolStripMenuItem` **没有** `ThreeState` 属性。

## 选择 / 点击

5. **取消选中不能靠 `SelectedIndex` 判断。** WinForms 在 `MouseDown` 之前就把
   `SelectedIndex` 改成新项了，写成"点到的是不是当前选中项"永远成立，一点就被取消。
   正确做法：记住"上一次点的是哪一行"。

6. 自定义绘制的列表行，鼠标悬停要用 `_hoverIndex` + `Invalidate()` 手动处理，
   并给 `Cursors.Hand`，否则看不出可点。

## 滚动条

7. **不要用 `SetWindowLong` 去掉 `WS_VSCROLL`**：滚动条没了，滚轮也一起废了。
   要"无边滚动条"就把控件加宽，再用父容器裁掉右侧 18px。

## 绘制 / 坐标

8. **视野裁剪矩形必须从屏幕 (0,0) 起算**，不能锚在 `origin` 上。
   缩放/平移后 `origin` 是负数，从它起算会把所有图元判成"在视野外"，
   表现为"放大一点标记就全消失"。

9. 画在地图上的东西要分层：背景图 → 静态标记 → 任务区域 → 撤离点 → 玩家，
   不然任务区域会被物资点盖住。

## 数据

10. 见 `docs/DATA-RULES.md`：先探接口、把字段给用户看，再实现。

## 一条通用感觉

11. 用户说的"不好用"通常不是审美问题，而是**控件默认行为和直觉不符**
    （点一下关、点父项不生效、改了不刷新）。遇到这类反馈先想
    "哪个默认行为在跟我作对"，而不是加更多代码。
