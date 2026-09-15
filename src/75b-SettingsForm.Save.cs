using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SnapWheel
{
    // 设置窗口的"保存"逻辑（从 75-SettingsForm.cs 拆出来，纯搬移，行为不变）。
    // 它把界面上所有控件的当前值写回 Settings —— 是整个窗口里最需要保持与 UI 一致的一段，
    // 单独放一个文件，改界面和改保存逻辑时不容易互相影响。
    partial class SettingsForm
    {
        // ============================ 保存 ============================
        // 把界面上改过的值写回设置。**只写"建过"的页**：没建过的页用户没看过，
        // 保持原值即可（绝不会把它覆盖回默认值 —— 老版本这里踩过一次"确定后改动打回原形"）。
        void SaveFromUi()
        {
            Settings s = _s;

            if (_built[0])
            {
                s.SaveToDisk = _chkDisk.Checked;
                s.Dir = _txtDir.Text.Trim();
                s.AutoHide = _chkAuto.Checked;
                s.AutoHideSeconds = (int)_numSec.Value;
                s.AlwaysOnTop = _chkTop.Checked;
                s.Corner = IndexCorner(_cmbCorner.SelectedIndex);
                s.AutoStart = _chkAutoStart.Checked;
                s.DeleteMode = (_cmbDel.SelectedIndex == 1) ? "single" : "double";
                s.SwitchMode = (_cmbSwitch.SelectedIndex == 1) ? "swipe" : "radial";
                s.ClipboardImport = _chkClip.Checked;
                s.CopyOnCapture = _chkCopy.Checked;
                s.ShowBalloon = _chkBalloon.Checked;
                s.CheckUpdate = _chkUpdate.Checked;
                s.DragOutAsFile = _chkDragFile.Checked;
                s.KeepAfterDragOut = _chkKeep.Checked;   // 0.6.0：拖出后是否留一份
                s.UiLanguage = (_cbLang.SelectedIndex == 2) ? "en" : (_cbLang.SelectedIndex == 1 ? "zh" : "");   // ""=跟随系统
            }
            if (_built[1])
            {
                s.MaxCount = (int)_numMax.Value;
                s.ThumbSize = (int)_numThumb.Value;
                s.Radius = (int)_numRad.Value;
                s.Slots = (int)_numSlots.Value;
                s.LabelSize = (int)_numLabel.Value;
                s.PeekPercent = (int)_numPeek.Value;
                s.UiScale = scVals[_cmbScale.SelectedIndex < 0 ? 0 : _cmbScale.SelectedIndex];
                s.CollapseMode = _chkCollapse.Checked;
                s.ExpandSpeed = ringVals[_cmbRing.SelectedIndex < 0 ? 2 : _cmbRing.SelectedIndex];
                s.CollapseSpeed = ringVals[_cmbRing2.SelectedIndex < 0 ? 2 : _cmbRing2.SelectedIndex];
                s.NubSingle = _chkSingle.Checked;
                s.ResetScrollOnCapture = _chkScrollReset.Checked;   // 0.5.3：截图后要不要把滚动位置重置到最新那张
            }
            if (_built[2])
            {
                s.UiStyle = (_cmbStyle.SelectedIndex == 1) ? "flat" : (_cmbStyle.SelectedIndex == 2 ? "solid" : "neu");
                s.AccentIndex = _cmbAccent.SelectedIndex - 1;
                s.AnimSpeed = (_cmbAnim.SelectedIndex == 0) ? 70 : (_cmbAnim.SelectedIndex == 2 ? 140 : 100);
                s.ShowNameLabel = _chkName.Checked;
                s.ShowCountLabel = _chkCount.Checked;
                s.GlassRefresh = _chkGlassRefresh.Checked;
                s.IntroAnim = _chkIntroAnim.Checked;     // 注意：这个控件在第 3 页（"动画细节"），别放进上一块
            }
            if (_built[3])
            {
                s.GlassPercent = (int)_numGlass.Value;
                s.CardRadius = (int)_numRadius.Value;
                s.ShadowPercent = (int)_numShadow.Value;
                s.PowerSave = _chkPower.Checked;     // 0.5.3：省电模式（电池上才实际生效，见 12-Power.cs）
                // 0.6.0：翻译接口（留空 = 用内置免费引擎链；填了 = 走你自己的 OpenAI 兼容接口）
                s.LlmUrl = _txtLlmUrl.Text.Trim();
                s.LlmKey = _txtLlmKey.Text.Trim();
                s.LlmModel = _txtLlmModel.Text.Trim();
                if (s.LlmModel.Length == 0) s.LlmModel = "deepseek-chat";   // 模型名空着会直接 400
                // 保存万能键四分区。这里必须立刻 s.Save() 落盘：
                // 否则下次打开设置窗口会从文件里读到旧值，一点确定就把刚改的打回原形
                // （"圆盘上的动作名改完不变"的根因）。这条行为现在由 tests\behavior-test.cs 守着。
                try
                {
                    for (int ki = 0; ki < 4; ki++)
                    {
                        if (_keyBox[ki] == null) continue;
                        int ksel = _keyBox[ki].SelectedIndex;
                        if (ksel < 0) ksel = 0;
                        s.SetKeyAction(ki, Settings.KeyActionIds[ksel]);
                    }
                    s.Save();
                }
                catch (Exception kex) { Err.Log("SettingsSaveKey", kex); }
            }
            if (_built[0] && _cmbHotkey != null && _cmbHotkey.SelectedItem != null) s.Hotkey = _cmbHotkey.SelectedItem.ToString();
            AutoRun.Apply(s.AutoStart);
            s.Save();
        }
    }
}
