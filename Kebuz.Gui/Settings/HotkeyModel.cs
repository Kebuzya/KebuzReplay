using System.ComponentModel;
using System.Runtime.CompilerServices;
using Kebuz.Gui.Localization;
using Kebuz.Recorder.Input;
using Kebuz.Recorder.Ipc;
using Kebuz.Recorder.Localization;
using Kebuz.Recorder.Settings;

namespace Kebuz.Gui.Settings;

public sealed class HotkeyModel : INotifyPropertyChanged
{
    private readonly Action commit;

    private string combination = KebuzSettings.Default.Hotkey;
    private string rejection = string.Empty;
    private HotkeyState? failure;
    private string idleKey = "Settings.Hotkey.Idle";
    private bool editing;

    public HotkeyModel(Action commit)
    {
        this.commit = commit;
        Loc.Track(this, static owner => ((HotkeyModel)owner).Relocalize());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Combination => combination;

    public bool Editing => editing;

    public string Text => editing ? Strings.Get("Settings.Hotkey.Press") : combination;

    public string Action => editing ? Strings.Get("Settings.Common.Cancel") : Strings.Get("Settings.Hotkey.Change");

    public string ActionName => editing
        ? Strings.Get("Settings.Hotkey.CancelName")
        : Strings.Get("Settings.Hotkey.ChangeName");

    public string Hint => editing
        ? Strings.Get("Settings.Hotkey.EditHint")
        : Strings.Get(idleKey);

    public bool HasComplaint => Complaint.Length > 0;

    public string Complaint => rejection.Length > 0
        ? rejection
        : failure is { } state
            ? Strings.Format("Hotkey.NotWorking", state.Combination, Strings.Render(state.Complaint))
            : string.Empty;

    public string IdleKey
    {
        get => idleKey;
        set
        {
            idleKey = value;
            Raise(nameof(Hint));
        }
    }

    public void Toggle()
    {
        editing = !editing;

        if (editing)
        {
            Dismiss();
        }

        RaiseAll();
    }

    public void Capture(int virtualKey, bool ctrl, bool alt, bool shift, bool win)
    {
        if (!editing || HotkeyRules.IsModifier(virtualKey))
        {
            return;
        }

        if (!HotkeyRules.TryFormat(virtualKey, ctrl, alt, shift, win, out var text, out var refusal))
        {
            rejection = refusal;
            RaiseAll();
            return;
        }

        editing = false;
        Dismiss();

        if (text == combination)
        {
            RaiseAll();
            return;
        }

        combination = text;
        RaiseAll();
        commit();
    }

    public void Adopt(string next)
    {
        combination = next;
        RaiseAll();
    }

    public void Note(HotkeyState state)
    {
        failure = state.Registered ? null : state;
        rejection = string.Empty;

        RaiseAll();
    }

    private void Dismiss()
    {
        rejection = string.Empty;
        failure = null;
    }

    private void Relocalize()
    {
        Raise(string.Empty);
    }

    private void RaiseAll()
    {
        Raise(nameof(Combination));
        Raise(nameof(Editing));
        Raise(nameof(Text));
        Raise(nameof(Action));
        Raise(nameof(ActionName));
        Raise(nameof(Hint));
        Raise(nameof(HasComplaint));
        Raise(nameof(Complaint));
    }

    private void Raise([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
