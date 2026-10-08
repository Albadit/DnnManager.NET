using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using DnnManager.Application.Configuration;
using DnnManager.Presentation.Services;

namespace DnnManager.IntegrationTests;

/// <summary>Animations on and off (Settings → General → Animations): what plays, and what changes at once instead.</summary>
[TestClass]
public sealed class MotionTests
{
    [TestCleanup]
    public void Cleanup() => Motion.Apply(true);

    [TestMethod]
    public void TheSetting_IsOnByDefault() => Assert.IsTrue(new AppearanceSettings().Animations);

    [TestMethod]
    public void TheSettingOff_TurnsMotionOff_AndSaysSo()
    {
        var changes = 0;
        void Count() => changes++;
        Motion.Changed += Count;
        try
        {
            Motion.Apply(false);
            Assert.IsFalse(Motion.Enabled);
            Motion.Apply(false);
            Assert.AreEqual(1, changes, "The same value again is no change.");
            Motion.Apply(true);
            Assert.AreEqual(SystemParameters.ClientAreaAnimation, Motion.Enabled, "On, as far as Windows' animation effects let it.");
            Assert.AreEqual(2, changes);
        }
        finally
        {
            Motion.Changed -= Count;
        }
    }

    [TestMethod]
    public void WhatPulses_OnlyPlays_WithMotion()
    {
        var whileSeen = EfficiencyMode.WhileSeen;
        object Seen(params object[] values) => whileSeen.Convert(values, typeof(bool), null!, CultureInfo.InvariantCulture);

        Assert.AreEqual(true, Seen(true, true, false), "Without the motion value, as before.");
        Assert.AreEqual(true, Seen(true, true, false, false));
        Assert.AreEqual(false, Seen(true, true, false, true), "Animations off: it stays still.");
        Assert.AreEqual(false, Seen(true, true, true, false), "Saving resources: still too.");
    }

    [TestMethod]
    public void AStateTheTemplateHasnt_IsIgnored_NotAnError()
    {
        // A switch goes to "Normal", "MouseOver", "Focused"… - its template only has Checked and Unchecked. The custom
        // manager is asked for every one of them; one it hasn't got stopped DNN Manager starting.
        Exception? failed = null;
        bool? unknown = null, known = null;
        var thread = new Thread(() =>
        {
            try
            {
                var root = new System.Windows.Controls.Grid();
                VisualStateManager.SetCustomVisualStateManager(root, new MotionStates());
                var group = new VisualStateGroup { Name = "CheckStates" };
                group.States.Add(new VisualState { Name = "Checked" });
                VisualStateManager.GetVisualStateGroups(root).Add(group);
                unknown = VisualStateManager.GoToElementState(root, "MouseOver", useTransitions: true);
                known = VisualStateManager.GoToElementState(root, "Checked", useTransitions: true);
            }
            catch (Exception ex) { failed = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.IsNull(failed, failed?.ToString());
        Assert.AreEqual(false, unknown);
        Assert.AreEqual(true, known);
    }

    [TestMethod]
    public void Popups_FadeIn_OnlyWithMotion()
    {
        Assert.AreEqual(PopupAnimation.Fade, Motion.PopupFade.Convert(false, typeof(PopupAnimation), null!, CultureInfo.InvariantCulture));
        Assert.AreEqual(PopupAnimation.None, Motion.PopupFade.Convert(true, typeof(PopupAnimation), null!, CultureInfo.InvariantCulture));
    }
}
