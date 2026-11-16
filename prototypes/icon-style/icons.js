// Mindweave icon-style prototype: icon data.
//
// CURRENT holds the app's geometry verbatim (src/Mindweave.Desktop/Themes/Icons.axaml and the inline
// PathIcon data in the views, as of main 25925d5). They are fill-only paths on a 16 × 16 grid.
//
// STROKE is one redrawn set on the same 16 × 16 grid, drawn as centre-line strokes. Directions A and B
// both use it; only the rendering differs:
//   A  rounded outline   every element stroked (round caps and joins); class "f" elements hidden
//   B  soft duotone      as A, plus class "f" elements filled with the same colour at low opacity,
//                        and class "s" (secondary) strokes drawn at reduced opacity
// Class "solid" elements are filled, never stroked, in A and B (the stop square, the list bullets).
// Direction C (current shapes, rounded corners only) is rendered from CURRENT with an SVG filter;
// see index.html.
//
// Every STROKE glyph is original to this prototype (no open icon set was copied), so no third-party
// licence applies. See README.md.

const CURRENT = {
  // ---- Themes/Icons.axaml (32) ----
  Sparkle: "M7,1 C7.5,4.6 9.4,6.5 13,7 C9.4,7.5 7.5,9.4 7,13 C6.5,9.4 4.6,7.5 1,7 C4.6,6.5 6.5,4.6 7,1 Z M13,10 C13.2,11.6 13.9,12.3 15.5,12.5 C13.9,12.7 13.2,13.4 13,15 C12.8,13.4 12.1,12.7 10.5,12.5 C12.1,12.3 12.8,11.6 13,10 Z",
  Rename: "M10.6,1.6 L14.4,5.4 L5.4,14.4 L1.5,14.5 L1.6,10.6 Z M3.1,11.2 L3,13 L4.8,12.9 L12.3,5.4 L10.6,3.7 Z",
  Summary: "M3,1 H13 V15 H3 Z M4.5,2.5 V13.5 H11.5 V2.5 Z M6,4.8 H10 V6.1 H6 Z M6,7.4 H10 V8.7 H6 Z M6,10 H8.6 V11.3 H6 Z",
  Summarize: "M2,2.5 H14 V4 H2 Z M2,6.5 H11.5 V8 H2 Z M2,10.5 H9 V12 H2 Z M12.5,9 L15,11.25 L12.5,13.5 Z",
  Archive: "M1,2 H15 V6 H1 Z M2.5,3.4 V4.6 H13.5 V3.4 Z M2,7 H3.5 V13.5 H12.5 V7 H14 V15 H2 Z M6,8.5 H10 V9.9 H6 Z",
  Delete: "M5.5,1 H10.5 V2.5 H14 V4 H2 V2.5 H5.5 Z M3,5 H13 L12.2,15 H3.8 Z M4.6,6.5 L5.2,13.5 H10.8 L11.4,6.5 Z M6.4,7.5 H7.6 V12.5 H6.4 Z M8.4,7.5 H9.6 V12.5 H8.4 Z",
  Regenerate: "M8,2 A6,6 0 1 0 14,8 H12.5 A4.5,4.5 0 1 1 8,3.5 V5.5 L11,2.75 L8,0 Z",
  Stop: "M3.5,3.5 H12.5 V12.5 H3.5 Z",
  Return: "M6,2 L1,6.5 L6,11 V8 H10 A3,3 0 0 1 10,14 H8 V15.5 H10 A4.5,4.5 0 0 0 10,6.5 H6 Z",
  Branch: "M3.5,1 H5 V7.5 C5,9.6 6.6,11 8.8,11 H11 V8.6 L14.8,11.75 L11,14.9 V12.5 H8.8 C5.8,12.5 3.5,10.4 3.5,7.5 Z",
  Chevron: "M5.5,2.5 L11,8 L5.5,13.5 L4.4,12.4 L8.8,8 L4.4,3.6 Z",
  Plus: "M7.25,2 H8.75 V7.25 H14 V8.75 H8.75 V14 H7.25 V8.75 H2 V7.25 H7.25 Z",
  Back: "M7,2.5 L1.5,8 L7,13.5 L8.06,12.44 L4.37,8.75 H14.5 V7.25 H4.37 L8.06,3.56 Z",
  Explore: "M2,2 H14 V11.5 H7 L4,14.5 V11.5 H2 Z M3.5,3.5 V10 H5.5 V10.9 L6.4,10 H12.5 V3.5 Z",
  SummaryNetwork: "M3,7.3 L13,2.3 L13,3.7 L3,8.7 Z M3,7.3 L13,12.3 L13,13.7 L3,8.7 Z M3,5.5 A2.5,2.5 0 1 1 3,10.5 A2.5,2.5 0 1 1 3,5.5 Z M13,1 A2,2 0 1 1 13,5 A2,2 0 1 1 13,1 Z M13,11 A2,2 0 1 1 13,15 A2,2 0 1 1 13,11 Z",
  Settings: "M1.5,3.25 H14.5 V4.75 H1.5 Z M1.5,11.25 H14.5 V12.75 H1.5 Z M5,1.75 A2.25,2.25 0 1 1 5,6.25 A2.25,2.25 0 1 1 5,1.75 Z M11,9.75 A2.25,2.25 0 1 1 11,14.25 A2.25,2.25 0 1 1 11,9.75 Z",
  TreeBranch: "M2,0 H3.4 V6.3 H9 V7.7 H2 Z",
  Sidebar: "M1,2.5 H15 V13.5 H1 Z M2.4,3.9 V12.1 H5.3 V3.9 Z M6.7,3.9 V12.1 H13.6 V3.9 Z",
  Sun: "M8,5 A3,3 0 1 1 8,11 A3,3 0 1 1 8,5 Z M7.3,0.6 H8.7 V3 H7.3 Z M7.3,13 H8.7 V15.4 H7.3 Z M0.6,7.3 H3 V8.7 H0.6 Z M13,7.3 H15.4 V8.7 H13 Z M11.75,5.25 L13.16,3.82 L12.17,2.84 L10.76,4.26 Z M4.25,5.25 L2.84,3.82 L3.83,2.84 L5.24,4.26 Z M11.75,10.75 L13.16,12.18 L12.17,13.16 L10.76,11.74 Z M4.25,10.75 L2.84,12.18 L3.83,13.16 L5.24,11.74 Z",
  Moon: "M14.2,9.3 A6.3,6.3 0 1 1 6.7,1.8 A4.9,4.9 0 0 0 14.2,9.3 Z",
  Monitor: "M1.5,2 H14.5 V11.5 H1.5 Z M3,3.5 V10 H13 V3.5 Z M7.25,11.5 H8.75 V13 H7.25 Z M4.75,13 H11.25 V14.5 H4.75 Z",
  ChevronDown: "M2.5,5.5 L8,11 L13.5,5.5 L12.4,4.4 L8,8.8 L3.6,4.4 Z",
  Folder: "M1,2.5 H6.2 L7.7,4 H15 V13.5 H1 Z M2.5,5.5 V12 H13.5 V5.5 Z",
  Storage: "M1.5,2 H14.5 V6.5 H1.5 Z M3,3.5 V5 H13 V3.5 Z M1.5,9 H14.5 V13.5 H1.5 Z M3,10.5 V12 H13 V10.5 Z M10.5,3.6 H12 V4.9 H10.5 Z M10.5,10.6 H12 V11.9 H10.5 Z",
  Chart: "M1.5,13.5 H14.5 V15 H1.5 Z M2.5,8 H5 V12.5 H2.5 Z M6.75,2.5 H9.25 V12.5 H6.75 Z M11,5.5 H13.5 V12.5 H11 Z",
  Copy: "M5,1 H14 V11 H5 Z M6.5,2.5 V9.5 H12.5 V2.5 Z M2,5 H3.5 V13.5 H10 V15 H2 Z",
  Select: "M2,2 H14 V14 H2 Z M3.5,3.5 V12.5 H12.5 V3.5 Z M10.6,5.4 L11.6,6.4 L7.2,10.8 L4.6,8.2 L5.6,7.2 L7.2,8.8 Z",
  Close: "M3.6,2.5 L8,6.9 L12.4,2.5 L13.5,3.6 L9.1,8 L13.5,12.4 L12.4,13.5 L8,9.1 L3.6,13.5 L2.5,12.4 L6.9,8 L2.5,3.6 Z",
  Eye: "M8,3 C11.5,3 14.2,5.6 15.5,8 C14.2,10.4 11.5,13 8,13 C4.5,13 1.8,10.4 0.5,8 C1.8,5.6 4.5,3 8,3 Z M8,4.5 C5.4,4.5 3.4,6.3 2.2,8 C3.4,9.7 5.4,11.5 8,11.5 C10.6,11.5 12.6,9.7 13.8,8 C12.6,6.3 10.6,4.5 8,4.5 Z M8,5.8 A2.2,2.2 0 1 1 8,10.2 A2.2,2.2 0 1 1 8,5.8 Z",
  Link: "M6.2,8.9 L8.9,6.2 L9.8,7.1 L7.1,9.8 Z M2.9,9.3 L5,7.2 L5.9,8.1 L3.8,10.2 A1.6,1.6 0 0 0 6,12.4 L8.1,10.3 L9,11.2 L6.9,13.3 A2.9,2.9 0 0 1 2.9,9.3 Z M13.1,6.7 L11,8.8 L10.1,7.9 L12.2,5.8 A1.6,1.6 0 0 0 10,3.6 L7.9,5.7 L7,4.8 L9.1,2.7 A2.9,2.9 0 0 1 13.1,6.7 Z",
  Check: "M13.4,3.6 L14.6,4.8 L6.4,13 L1.6,8.2 L2.8,7 L6.4,10.6 Z",
  Workspaces: "M1.5,1.5 H7 V7 H1.5 Z M3,3 V5.5 H5.5 V3 Z M9,1.5 H14.5 V7 H9 Z M10.5,3 V5.5 H13 V3 Z M1.5,9 H7 V14.5 H1.5 Z M3,10.5 V13 H5.5 V10.5 Z M9,9 H14.5 V14.5 H9 Z M10.5,10.5 V13 H13 V10.5 Z",
  // ---- inline PathIcon data in the views (not in Icons.axaml) ----
  SaveLayout: "M2,1 H12 L15,4 V15 H1 V1 Z M4,1 V5 H11 V1 Z M3,9 H13 V14 H3 Z",
  ShowAll: "M1,1 H6 V3 H3 V6 H1 Z M10,1 H15 V6 H13 V3 H10 Z M1,10 H3 V13 H6 V15 H1 Z M13,10 H15 V15 H10 V13 H13 Z",
  ZoomOut: "M1,7 H15 V9 H1 Z",
  ZoomIn: "M7,1 H9 V7 H15 V9 H9 V15 H7 V9 H1 V7 H7 Z",
  Arrange: "M1,6 H5 V10 H1 Z M11,1 H15 V5 H11 Z M11,11 H15 V15 H11 Z M5,7.25 H10 V8.75 H5 Z M9,2.25 H11 V3.75 H9 Z M9,12.25 H11 V13.75 H9 Z M8.25,2.25 H9.75 V13.75 H8.25 Z",
  ListView: "M1,2 H3 V4 H1 Z M5,2 H15 V4 H5 Z M1,7 H3 V9 H1 Z M5,7 H15 V9 H5 Z M1,12 H3 V14 H1 Z M5,12 H15 V14 H5 Z",
  ChevronLeft: "M10,2 L4,8 L10,14 L11.5,12.5 L7,8 L11.5,3.5 Z",
  Edit: "M11,1 L15,5 L5.5,14.5 H1.5 V10.5 Z M11,3.1 L3,11.1 V13 H4.9 L12.9,5 Z",
  RenameText: "M1,2 H10 V4 H6.5 V13 H4.5 V4 H1 Z M9,9 H15 V11 H9 Z M9,12.5 H13 V14.5 H9 Z",
  Download: "M7,1 H9 V8 L11.5,5.5 L12.9,6.9 L8,11.8 L3.1,6.9 L4.5,5.5 L7,8 Z M1,10 H3 V13 H13 V10 H15 V15 H1 Z",
};

// Where each glyph appears (views and styles), for the inventory table.
const USES = {
  Sparkle: "新会话 name suggestions; 工作区 page; network recommendations",
  Rename: "conversation toolbar 改名; sidebar row menu; link suggestions; 工作区 page",
  Summary: "conversation toolbar: the summary entry",
  Summarize: "conversation toolbar 总结此会话; sidebar row menu",
  Archive: "conversation toolbar; sidebar selection bar; 已归档 page; window dialogs",
  Delete: "conversation toolbar; sidebar selection bar; 已归档; 工作区; storage details",
  Regenerate: "regenerate answer; 换一批推荐; name suggestions; settings",
  Stop: "composer stop button (34 × 34 circle)",
  Return: "恢复 on 已归档; back to the source conversation",
  Branch: "branch entries under an answer (AnswerView)",
  Chevron: "branch-entry arrow; settings rows",
  Plus: "sidebar 新会话; workspace +; 新建工作区…; 探索 home",
  Back: "page header back",
  Explore: "sidebar 探索; 探索 home; row menu",
  SummaryNetwork: "sidebar 总结网络; first-run mark",
  Settings: "sidebar 设置",
  TreeBranch: "sidebar branch rows; 已归档 tree",
  Sidebar: "收起侧栏 / expand",
  Sun: "settings 外观 segmented control",
  Moon: "settings 外观 segmented control",
  Monitor: "settings 外观 segmented control",
  ChevronDown: "workspace switcher; 关联 menu; settings model list",
  Folder: "storage details 打开",
  Storage: "settings: 数据与存储",
  Chart: "settings: usage",
  Copy: "copy button (CopyButton.cs)",
  Select: "sidebar 选择 toggle",
  Close: "clear selection; close notice; 新会话 suggestion; link picker",
  Eye: "network 隐藏推荐 / 隐藏关联",
  Link: "network 关联 menu; 关联到…; links in details",
  Check: "copied; switcher current row; settings row check; sidebar",
  Workspaces: "管理工作区 (switcher menu, settings)",
  SaveLayout: "network 保存布局 (inline)",
  ShowAll: "network 显示全部 (inline)",
  ZoomOut: "network 缩小 (inline)",
  ZoomIn: "network 放大 (inline)",
  Arrange: "network 整理布局 (inline)",
  ListView: "network 列表 (inline)",
  ChevronLeft: "summary page 查看当前 (inline)",
  Edit: "summary page 编辑 (inline)",
  RenameText: "summary page 改名 (inline)",
  Download: "summary page version menu (inline)",
};

// The redrawn set (directions A and B). 16 × 16 grid, centre-line strokes.
const STROKE = {
  Sparkle: `<path class="f" d="M7 2.25C7.45 5.35 8.65 6.55 11.75 7 8.65 7.45 7.45 8.65 7 11.75 6.55 8.65 5.35 7.45 2.25 7 5.35 6.55 6.55 5.35 7 2.25Z"/>
    <path d="M7 2.25C7.45 5.35 8.65 6.55 11.75 7 8.65 7.45 7.45 8.65 7 11.75 6.55 8.65 5.35 7.45 2.25 7 5.35 6.55 6.55 5.35 7 2.25Z"/>
    <path class="s" d="M12.5 10.75V14.25M10.75 12.5H14.25"/>`,
  Rename: `<path class="f" d="M2.5 13.5 3 10.75 10.75 3a1.6 1.6 0 0 1 2.25 2.25L5.25 13Z"/>
    <path d="M2.5 13.5 3 10.75 10.75 3a1.6 1.6 0 0 1 2.25 2.25L5.25 13Z"/><path class="s" d="M9.25 4.5 11.5 6.75"/>`,
  Summary: `<rect class="f" x="3" y="1.75" width="10" height="12.5" rx="2"/>
    <rect x="3" y="1.75" width="10" height="12.5" rx="2"/><path class="s" d="M5.75 5.5H10.25M5.75 8H10.25M5.75 10.5H8.25"/>`,
  Summarize: `<path class="s" d="M2.5 3.5H13.5M2.5 7.5H10.5"/><path d="M2.5 11.5H8M11.5 9.5 13.75 11.75 11.5 14"/>`,
  Archive: `<rect class="f" x="1.75" y="2.25" width="12.5" height="3.5" rx="1.25"/>
    <rect x="1.75" y="2.25" width="12.5" height="3.5" rx="1.25"/><path d="M3 5.75V12a2 2 0 0 0 2 2h6a2 2 0 0 0 2-2V5.75"/><path class="s" d="M6.5 8.75H9.5"/>`,
  Delete: `<path class="f" d="M3.5 4.25 4.4 12.6A1.6 1.6 0 0 0 6 14h4a1.6 1.6 0 0 0 1.6-1.4l.9-8.35Z"/>
    <path d="M2.25 4.25H13.75M6 4.25V3a1 1 0 0 1 1-1h2a1 1 0 0 1 1 1v1.25M3.5 4.25 4.4 12.6A1.6 1.6 0 0 0 6 14h4a1.6 1.6 0 0 0 1.6-1.4l.9-8.35"/>
    <path class="s" d="M6.75 7V11M9.25 7V11"/>`,
  Regenerate: `<path d="M13.25 8A5.25 5.25 0 1 1 12.02 4.63"/><path d="M12.02 1.9V4.63H9.3"/>`,
  Stop: `<rect class="solid" x="3.5" y="3.5" width="9" height="9" rx="2.25"/>`,
  Return: `<path d="M5.5 2.75 2.25 6 5.5 9.25"/><path d="M2.5 6H10a3.5 3.5 0 0 1 0 7H8"/>`,
  Branch: `<circle class="f" cx="4.25" cy="3" r="1.25"/><path d="M4.25 1.75V7a4.5 4.5 0 0 0 4.5 4.5h4.75"/><path d="M11 9 13.5 11.5 11 14"/>`,
  Chevron: `<path d="M6 3.5 10.5 8 6 12.5"/>`,
  Plus: `<path d="M8 3V13M3 8H13"/>`,
  Back: `<path d="M7 3.5 2.5 8 7 12.5M2.75 8H13.5"/>`,
  Explore: `<path class="f" d="M3.75 2.5h8.5a2 2 0 0 1 2 2v5a2 2 0 0 1-2 2H7.5l-3 2.5v-2.5h-.75a2 2 0 0 1-2-2v-5a2 2 0 0 1 2-2Z"/>
    <path d="M3.75 2.5h8.5a2 2 0 0 1 2 2v5a2 2 0 0 1-2 2H7.5l-3 2.5v-2.5h-.75a2 2 0 0 1-2-2v-5a2 2 0 0 1 2-2Z"/>`,
  SummaryNetwork: `<circle class="f" cx="3.75" cy="8" r="2"/><circle class="f" cx="12.25" cy="3.5" r="1.75"/><circle class="f" cx="12.25" cy="12.5" r="1.75"/>
    <circle cx="3.75" cy="8" r="2"/><circle cx="12.25" cy="3.5" r="1.75"/><circle cx="12.25" cy="12.5" r="1.75"/>
    <path class="s" d="M5.55 7.05 10.7 4.32M5.55 8.95 10.7 11.68"/>`,
  Settings: `<circle class="f" cx="5.5" cy="4.5" r="1.75"/><circle class="f" cx="10.5" cy="11.5" r="1.75"/>
    <path class="s" d="M1.75 4.5H3.75M7.25 4.5H14.25M1.75 11.5H8.75M12.25 11.5H14.25"/>
    <circle cx="5.5" cy="4.5" r="1.75"/><circle cx="10.5" cy="11.5" r="1.75"/>`,
  TreeBranch: `<path d="M2.75 0.75V5a2.5 2.5 0 0 0 2.5 2.5H9"/>`,
  Sidebar: `<path class="f" d="M6 2.5H4A2.25 2.25 0 0 0 1.75 4.75v6.5A2.25 2.25 0 0 0 4 13.5H6Z"/>
    <rect x="1.75" y="2.5" width="12.5" height="11" rx="2.25"/><path d="M6 2.5V13.5"/>`,
  Sun: `<circle class="f" cx="8" cy="8" r="2.75"/><circle cx="8" cy="8" r="2.75"/>
    <path class="s" d="M8 1.5V2.75M8 13.25V14.5M1.5 8H2.75M13.25 8H14.5M3.4 3.4 4.28 4.28M11.72 11.72 12.6 12.6M3.4 12.6 4.28 11.72M11.72 4.28 12.6 3.4"/>`,
  Moon: `<path class="f" d="M13.75 9.6A5.9 5.9 0 1 1 6.4 2.25 4.6 4.6 0 0 0 13.75 9.6Z"/><path d="M13.75 9.6A5.9 5.9 0 1 1 6.4 2.25 4.6 4.6 0 0 0 13.75 9.6Z"/>`,
  Monitor: `<rect class="f" x="1.75" y="2.25" width="12.5" height="9" rx="2"/>
    <rect x="1.75" y="2.25" width="12.5" height="9" rx="2"/><path class="s" d="M8 11.25V13.75M5.25 13.75H10.75"/>`,
  ChevronDown: `<path d="M3.5 6 8 10.5 12.5 6"/>`,
  Folder: `<path class="f" d="M1.75 4.25a2 2 0 0 1 2-2H6l1.75 1.75h4.5a2 2 0 0 1 2 2v5.75a2 2 0 0 1-2 2h-8.5a2 2 0 0 1-2-2Z"/>
    <path d="M1.75 4.25a2 2 0 0 1 2-2H6l1.75 1.75h4.5a2 2 0 0 1 2 2v5.75a2 2 0 0 1-2 2h-8.5a2 2 0 0 1-2-2Z"/>`,
  Storage: `<rect class="f" x="1.75" y="2" width="12.5" height="5" rx="1.75"/><rect class="f" x="1.75" y="9" width="12.5" height="5" rx="1.75"/>
    <rect x="1.75" y="2" width="12.5" height="5" rx="1.75"/><rect x="1.75" y="9" width="12.5" height="5" rx="1.75"/>
    <path d="M11 4.5H11.01M11 11.5H11.01"/>`,
  Chart: `<path class="s" d="M1.75 14.25H14.25"/><path d="M4 11.75V8.5M8 11.75V3.75M12 11.75V6.5"/>`,
  Copy: `<rect class="f" x="5.25" y="1.75" width="9" height="9" rx="2"/>
    <rect x="5.25" y="1.75" width="9" height="9" rx="2"/><path class="s" d="M2.25 5.5V12.25a2 2 0 0 0 2 2H10.5"/>`,
  Select: `<rect class="f" x="2" y="2" width="12" height="12" rx="2.5"/>
    <rect x="2" y="2" width="12" height="12" rx="2.5"/><path d="M5.25 8.25 7.25 10.25 10.75 6"/>`,
  Close: `<path d="M4 4 12 12M12 4 4 12"/>`,
  Eye: `<path class="f" d="M1.5 8C3 5 5.3 3.5 8 3.5S13 5 14.5 8C13 11 10.7 12.5 8 12.5S3 11 1.5 8Z"/>
    <path d="M1.5 8C3 5 5.3 3.5 8 3.5S13 5 14.5 8C13 11 10.7 12.5 8 12.5S3 11 1.5 8Z"/><circle cx="8" cy="8" r="2"/>`,
  Link: `<path d="M7 5 8.6 3.4A2.83 2.83 0 0 1 12.6 7.4L11 9M9 11 7.4 12.6A2.83 2.83 0 0 1 3.4 8.6L5 7"/><path class="s" d="M6.25 9.75 9.75 6.25"/>`,
  Check: `<path d="M3 8.5 6.5 12 13 4.5"/>`,
  Workspaces: `<rect class="f" x="1.75" y="1.75" width="5" height="5" rx="1.5"/>
    <rect x="1.75" y="1.75" width="5" height="5" rx="1.5"/><rect x="9.25" y="1.75" width="5" height="5" rx="1.5"/>
    <rect x="1.75" y="9.25" width="5" height="5" rx="1.5"/><rect x="9.25" y="9.25" width="5" height="5" rx="1.5"/>`,
  SaveLayout: `<path class="f" d="M3.75 1.75H10.5l3.75 3.75v6.75a2 2 0 0 1-2 2h-8.5a2 2 0 0 1-2-2v-8.5a2 2 0 0 1 2-2Z"/>
    <path d="M3.75 1.75H10.5l3.75 3.75v6.75a2 2 0 0 1-2 2h-8.5a2 2 0 0 1-2-2v-8.5a2 2 0 0 1 2-2Z"/>
    <path class="s" d="M5 1.75V4.5H9.5V1.75M4.75 14.25V10.25H11.25V14.25"/>`,
  ShowAll: `<path d="M1.75 5.5V3.75a2 2 0 0 1 2-2H5.5M10.5 1.75h1.75a2 2 0 0 1 2 2V5.5M14.25 10.5v1.75a2 2 0 0 1-2 2H10.5M5.5 14.25H3.75a2 2 0 0 1-2-2V10.5"/>`,
  ZoomOut: `<path d="M3 8H13"/>`,
  ZoomIn: `<path d="M8 3V13M3 8H13"/>`,
  Arrange: `<rect class="f" x="1.75" y="6" width="4" height="4" rx="1.25"/><rect class="f" x="10.25" y="1.75" width="4" height="4" rx="1.25"/><rect class="f" x="10.25" y="10.25" width="4" height="4" rx="1.25"/>
    <rect x="1.75" y="6" width="4" height="4" rx="1.25"/><rect x="10.25" y="1.75" width="4" height="4" rx="1.25"/><rect x="10.25" y="10.25" width="4" height="4" rx="1.25"/>
    <path class="s" d="M5.75 8H8M10.25 3.75H9.5A1.5 1.5 0 0 0 8 5.25v5.5a1.5 1.5 0 0 0 1.5 1.5h.75"/>`,
  ListView: `<circle class="solid" cx="2.5" cy="3.5" r="1"/><circle class="solid" cx="2.5" cy="8" r="1"/><circle class="solid" cx="2.5" cy="12.5" r="1"/>
    <path d="M5.75 3.5H14M5.75 8H14M5.75 12.5H14"/>`,
  ChevronLeft: `<path d="M10 3.5 5.5 8 10 12.5"/>`,
  Edit: `<path class="f" d="M2.5 13.5 3 10.75 9.75 4a1.6 1.6 0 0 1 2.25 2.25L5.25 13Z"/>
    <path d="M2.5 13.5 3 10.75 9.75 4a1.6 1.6 0 0 1 2.25 2.25L5.25 13Z"/><path class="s" d="M8.5 13.5H13.75"/>`,
  RenameText: `<path d="M2 3H9.5M5.75 3V13"/><path class="s" d="M9.75 9.5H14M9.75 12.75H12.5"/>`,
  Download: `<path d="M8 1.75V9.75M4.75 6.5 8 9.75 11.25 6.5"/><path class="s" d="M2 10.75v1.5a2 2 0 0 0 2 2h8a2 2 0 0 0 2-2v-1.5"/>`,
};

const ORDER = Object.keys(CURRENT);
