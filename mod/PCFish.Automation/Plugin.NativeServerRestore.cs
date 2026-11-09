using System.Text.Json;
using NN.PF.Core.Managers;

namespace PCFish.Automation;

public sealed partial class Plugin
{
    NativeBreedRetry nativeBreedRetry = new();
    string NativeRetryPath => Path.Combine(root, "native-breed-retry.json");
    void LoadNativeBreedRetry()
    {
        if (!File.Exists(NativeRetryPath)) return;
        nativeBreedRetry = JsonSerializer.Deserialize<NativeBreedRetry>(ReadText(NativeRetryPath))
            ?? throw new InvalidDataException("Empty native retry journal");
        nativeBreedRetry.Validate();
        if (nativeBreedRetry.pending) nextActionAt = Math.Max(nextActionAt, nativeBreedRetry.not_before);
    }
    void SaveNativeBreedRetry() => Atomic(NativeRetryPath, nativeBreedRetry);

    bool RestoreNativeBreedInventory(string[] ids, Snapshot current, GameDataManager data, GameManager game, UIManager ui, Settings cfg)
    {
        var work = recovery;
        if (!NativeServerRestore.CanRestore(work, current, ids, AccountContext())) return false;
        if (applyVerifiedProbeRestore == null)
            throw new InvalidDataException("The exact verified native restore callback is unavailable");
        if (current.window_open || current.overlay_open || current.confirmation_open || current.level_up_popup_open)
        {
            inventoryPacing.next_at = Math.Max(inventoryPacing.next_at, Now + 30); SavePacing();
            message = "Server-absent result retained until the visible game interaction finishes; no new breeding sent";
            Status("waiting"); return true;
        }
        var originals = Path.Combine(root, "pre-server-restore-receipts"); Directory.CreateDirectory(originals);
        var original = Path.Combine(originals, work.cmd.request_id + ".json");
        if (!File.Exists(original)) File.Copy(RecoveryPath, original);
        work.server_restore_started_at = work.server_restore_started_at > 0 ? work.server_restore_started_at : Now;
        work.server_restore_probe_id = probeId;
        work.validation_failed = false;
        work.note = "Applying the original game server-data restoration before a new breeding attempt";
        SaveRecovery();
        NativeComparisonStage("original_server_restore_started", new { child_id = work.child_id,
            probe_id = probeId, hearts = current.hearts, fish_ids = current.fish.Select(f => f.id).ToArray() });
        // ManageRestore already issued this authenticated GET. Execute its
        // actual original success callback exactly once for this response.
        // No second GET, custom inventory editing, XP or heart adjustment.
        applyVerifiedProbeRestore();
        var restored = Capture(data, ui, current.config_digest, cfg);
        NativeServerRestore.Confirm(current, restored, ids, work.child_id);
        work.server_restore_completed_at = Now; work.validation_failed = false;
        work.note = "Original server-data restoration completed and verified; new breeding can be replanned";
        SaveRecovery();
        NativeComparisonStage("original_server_restore_completed", new { child_id = work.child_id,
            probe_id = probeId, before_hearts = current.hearts, after_hearts = restored.hearts,
            removed_local_ids = current.fish.Where(f => !restored.fish.Any(a => a.id == f.id)).Select(f => f.id).ToArray(),
            fish_ids = restored.fish.Select(f => f.id).ToArray(), native_restore_used = true,
            custom_resource_adjustment = false,
            before_breeds = current.fish.Select(f => new { f.id, f.remaining_breeds, f.locked }).ToArray(),
            after_breeds = restored.fish.Select(f => new { f.id, f.remaining_breeds, f.locked }).ToArray() });
        nativeBreedRetry.Queue(work.cmd.request_id, AccountContext(), Now, cfg.minimum_action_interval_seconds);
        SaveNativeBreedRetry();
        FinishRecoveredRequest("native_server_restore_after_missing_breed_uid", ids, current, data, game, cfg);
        nextActionAt = Math.Max(nextActionAt, nativeBreedRetry.not_before);
        if (nativeComparisonUi != null && !ui.viewWindow.activeInHierarchy && !nativeComparisonUi.gameObject.activeInHierarchy)
            nativeComparisonUi.Close();
        message = "Native server restoration verified; waiting before a new original-game breeding request";
        Status("waiting");
        return true;
    }
}
