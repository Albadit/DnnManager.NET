using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;

namespace DnnManager.Presentation.Controls;

/// <summary>
/// What a screen reader finds of a view that draws its own text (Logs, a terminal): a read-only document, with a fixed
/// name, whose value is the text on screen - read only when it is asked for. Nothing is raised as the text changes and
/// there are no child elements: a UI Automation client listening (Windows' own text input, an assistive tool) costs
/// the busy views nothing.
/// </summary>
internal sealed class TextViewPeer(FrameworkElement owner, string name, Func<string> visibleText)
    : FrameworkElementAutomationPeer(owner), IValueProvider
{
    protected override string GetClassNameCore() => Owner.GetType().Name;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;

    // A name the page gives it (AutomationProperties.Name) wins over the view's own.
    protected override string GetNameCore() => AutomationProperties.GetName(Owner) is { Length: > 0 } given ? given : name;

    protected override bool IsContentElementCore() => true;

    protected override bool IsKeyboardFocusableCore() => Owner.Focusable;

    protected override List<AutomationPeer>? GetChildrenCore() => null;

    public override object? GetPattern(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Value ? this : base.GetPattern(patternInterface);

    public bool IsReadOnly => true;

    public string Value => visibleText();

    public void SetValue(string value) => throw new InvalidOperationException("The text can't be changed.");
}
