using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace XIVibration;

/// <summary>
/// Hooks the game's action-effect handler. For the local player it fires when the server
/// confirms the action, which is also when the game starts the action's animation timeline.
/// So pulse delays line up with the animation the same way C120 entries in a .tmb do.
/// </summary>
public sealed unsafe class ActionWatcher : IDisposable
{
    private delegate void ReceiveDelegate(
        uint casterEntityId,
        Character* casterPtr,
        Vector3* targetPos,
        ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId* targetEntityIds);

    private readonly Hook<ReceiveDelegate>? hook;

    /// <summary>Action id, and the action timeline the character actually started (0 if unknown).</summary>
    public event Action<uint, uint>? LocalActionUsed;

    public ActionWatcher()
    {
        try
        {
            this.hook = Plugin.GameInterop.HookFromAddress<ReceiveDelegate>(
                ActionEffectHandler.Addresses.Receive.Value, this.Detour);
            this.hook.Enable();
        }
        catch (Exception ex)
        {
            this.LastError = ex.Message;
            Plugin.Log.Error(ex, "Could not hook ActionEffectHandler.Receive");
        }
    }

    public bool HookActive => this.hook is { IsEnabled: true };

    public string? LastError { get; }

    /// <summary>Most recent first.</summary>
    public List<uint> Recent { get; } = new();

    private void Detour(
        uint casterEntityId,
        Character* casterPtr,
        Vector3* targetPos,
        ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId* targetEntityIds)
    {
        this.hook!.Original(casterEntityId, casterPtr, targetPos, header, effects, targetEntityIds);

        try
        {
            var local = Plugin.ObjectTable.LocalPlayer;
            if (local == null || header == null || casterEntityId != local.EntityId)
                return;

            // ActionType 1 = normal action (skills, spells, abilities).
            if (header->ActionType != 1)
                return;

            var actionId = header->ActionId;
            if (actionId == 0)
                return;

            this.Recent.Remove(actionId);
            this.Recent.Insert(0, actionId);
            if (this.Recent.Count > 20)
                this.Recent.RemoveAt(this.Recent.Count - 1);

            this.LocalActionUsed?.Invoke(actionId, PlayedTimeline(casterPtr, actionId));
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Error in action effect handler");
        }
    }

    /// <summary>Reads the timeline the game just started on the caster, in the slot the action's animation uses.</summary>
    private static uint PlayedTimeline(Character* caster, uint actionId)
    {
        try
        {
            if (caster == null)
                return 0;
            var actions = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
            if (!actions.TryGetRow(actionId, out var action) || !action.AnimationEnd.IsValid)
                return 0;
            var slot = action.AnimationEnd.Value.Slot;
            return caster->Timeline.TimelineSequencer.GetSlotTimeline(slot);
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose() => this.hook?.Dispose();
}
