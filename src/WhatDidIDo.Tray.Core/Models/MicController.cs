using WhatDidIDo.Tray.Core.Control;

namespace WhatDidIDo.Tray.Core.Models;

/// <summary>Where the microphone choice is kept (TRAY.md section 6).</summary>
public interface IMicChoiceStore
{
    PreferredMic LoadMicChoice();

    void SaveMicChoice(PreferredMic choice);
}

/// <summary>How a toggle or a pick ended. Other failures throw (shown as the flyout's notice).</summary>
public enum MicResult
{
    /// <summary>No saved choice: nothing sent, the input picker opens.</summary>
    Choose,
    /// <summary>The saved choice is not connected: nothing sent.</summary>
    Missing,
    /// <summary>A later "off" came first: nothing sent.</summary>
    Superseded,
    /// <summary>A pick only saved the choice.</summary>
    Saved,
    On,
    Off,
}

/// <summary>
/// The flyout's microphone decisions, ported from the extension's
/// <c>MicController</c>. Only <see cref="ToggleAsync"/> and
/// <see cref="ChooseAsync"/> (user actions) ever turn the microphone on or
/// off; status updates and inventory reads never do. Their requests run one
/// after another, and the last intent wins: an "on" that a later "off"
/// superseded is not sent. Its awaits resume on the caller's context, so on
/// the UI thread every state change and <c>changed</c> call stays there.
/// </summary>
public sealed class MicController(
    IMicChoiceStore store,
    Func<bool, PreferredMic, CancellationToken, Task> setMicOn,
    Func<CancellationToken, Task<SessionStatus>> readStatus,
    Action? openSources = null,
    Action? changed = null) : IDisposable
{
    // The session states in which a pick only saves the choice (the tile is insensitive then too).
    private static readonly string[] TransientStates = ["starting", "stopping"];

    private readonly SemaphoreSlim _serial = new(1, 1);
    private int _seq;       // numbers each toggle/choose
    private int _lastOff;   // the latest toggle(false)

    /// <summary>The last inventory, or null when unknown.</summary>
    public IReadOnlyList<MicItem>? Mics { get; private set; }

    public SessionStatus? Status { get; private set; }
    public bool Running { get; private set; }

    /// <summary>"Choose a microphone" until a pick or the flyout closes.</summary>
    public bool Prompt { get; private set; }

    /// <summary>The client refused the saved choice as not connected.</summary>
    public bool Missing { get; private set; }

    public PreferredMic Preferred => store.LoadMicChoice();

    public void Dispose() => _serial.Dispose();

    public void SetInventory(IReadOnlyList<MicItem>? mics)
    {
        Mics = mics;
        if (MicModel.MigrateStableId(Mics, Preferred) is { } stable)
            store.SaveMicChoice(Preferred with { StableId = stable });
        if (Mics is not null && MicModel.Choose(Mics, Preferred) is not null)
            Missing = false;
        changed?.Invoke();
    }

    public void UpdateStatus(SessionStatus? status, bool running)
    {
        Status = status;
        Running = running;
        if (running && status?.Mic is { On: true })
            Prompt = Missing = false;
    }

    public void FlyoutClosed() => Prompt = false;

    /// <summary>The Microphone tile.</summary>
    public async Task<MicResult> ToggleAsync(bool on, CancellationToken ct = default)
    {
        var seq = Intent(on);
        Missing = false;
        if (on && !Preferred.HasChoice)
        {
            Prompt = true;
            changed?.Invoke();
            openSources?.Invoke();
            return MicResult.Choose;
        }
        Prompt = false;
        return await Serial(() => SendAsync(on, seq, ct), ct);
    }

    /// <summary>
    /// A source picked in the input picker: save it; turn the microphone on
    /// when the user was asked to choose, or switch a live microphone to
    /// another device. Decided from a fresh status (the last poll may predate a
    /// change on the session page); while starting or stopping it only saves.
    /// </summary>
    public async Task<MicResult> ChooseAsync(MicSource item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var seq = Intent(true);
        var prompt = Prompt;
        store.SaveMicChoice(new PreferredMic(item.StableId, item.Id, item.Label));
        Prompt = Missing = false;
        changed?.Invoke();
        return await Serial(async () =>
        {
            if (_lastOff > seq)
                return MicResult.Saved;
            var status = await readStatus(ct);
            UpdateStatus(status, true);
            if (status.State is { } state && TransientStates.Contains(state))
                return MicResult.Saved;
            if (!prompt && !MicModel.LiveSwitchNeeded(status.Mic, true, item))
                return MicResult.Saved;
            return await SendAsync(true, seq, ct);
        }, ct);
    }

    public MicView View()
    {
        var preferred = Preferred;
        return new MicView(
            MicModel.SwitchLabel(Status, Running, Mics),
            MicModel.Hint(Status, Running, Mics, preferred, Prompt, Missing),
            MicModel.Sources(Mics, preferred));
    }

    private int Intent(bool on)
    {
        var seq = ++_seq;
        if (!on)
            _lastOff = seq;
        return seq;
    }

    private async Task<MicResult> SendAsync(bool on, int seq, CancellationToken ct)
    {
        if (on && _lastOff > seq)
            return MicResult.Superseded;
        try
        {
            await setMicOn(on, Preferred, ct);
        }
        catch (ControlException e) when (e.Code == "microphone_missing")
        {
            if (e.Microphones is not null)
                Mics = e.Microphones;
            Missing = true;
            changed?.Invoke();
            return MicResult.Missing;
        }
        return on ? MicResult.On : MicResult.Off;
    }

    private async Task<MicResult> Serial(Func<Task<MicResult>> run, CancellationToken ct)
    {
        await _serial.WaitAsync(ct);
        try
        {
            return await run();
        }
        finally
        {
            _serial.Release();
        }
    }
}
