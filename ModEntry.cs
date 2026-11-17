using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Objects;

namespace MarryOnTouch;

public sealed class ModEntry : Mod
{
    private static ModEntry instance = null!;
    private readonly MorningCheckpoint morning = new();
    private readonly WeddingScene wedding = new();
    private Contact? pending;
    private (GameLocation Location, BedFurniture Bed, Rectangle Area)? wakeUp;
    private bool needsCheckpoint;
    private bool returnForReload;
    private bool loadFromTitle;
    private bool warnedMultiplayer;

    public override void Entry(IModHelper helper)
    {
        instance = this;
        var harmony = new Harmony(ModManifest.UniqueID);
        harmony.Patch(
            AccessTools.Method(typeof(Farmer), "MovePositionImpl"),
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(BeforeMove)));
        harmony.Patch(
            AccessTools.Method(typeof(GameLocation), nameof(GameLocation.isCollidingPosition), new[]
            {
                typeof(Rectangle), typeof(xTile.Dimensions.Rectangle), typeof(bool), typeof(int),
                typeof(bool), typeof(Character), typeof(bool), typeof(bool), typeof(bool), typeof(bool)
            }),
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(BeforeNpcCollision)));
        harmony.Patch(
            AccessTools.Method(typeof(GameLocation), nameof(GameLocation.performTouchAction), new[] { typeof(string[]), typeof(Vector2) }),
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(BeforeSleepTouch)));
        harmony.Patch(
            AccessTools.Method(typeof(GraphicsDevice), nameof(GraphicsDevice.Present), Type.EmptyTypes),
            prefix: new HarmonyMethod(typeof(ModEntry), nameof(BeforePresent)) { priority = Priority.Last });
        wedding.Install(harmony);

        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
        helper.Events.GameLoop.UpdateTicking += OnUpdateTicking;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
    }

    private bool CanDetect =>
        morning.IsReady && !returnForReload && !wedding.IsRunning
        && Context.IsWorldReady && !Context.IsMultiplayer && Context.IsPlayerFree && !Game1.player.UsingTool
        && !Game1.fadeToBlack && !Game1.globalFade && Game1.locationRequest is null;

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        returnForReload = false;
        needsCheckpoint = true;
        pending = null;
        ProtectWakeUp();
    }

    private void OnDayStarted(object? sender, DayStartedEventArgs e)
    {
        needsCheckpoint = true;
        pending = null;
        ProtectWakeUp();
    }

    private void OnReturnedToTitle(object? sender, ReturnedToTitleEventArgs e)
    {
        pending = null;
        wakeUp = null;
        wedding.Clear();
        needsCheckpoint = false;
        warnedMultiplayer = false;
        if (!returnForReload)
            morning.Clear();
    }

    private void OnUpdateTicking(object? sender, UpdateTickingEventArgs e)
    {
        if (loadFromTitle)
        {
            loadFromTitle = false;
            Game1.activeClickableMenu = null;
            try
            {
                morning.Load();
            }
            catch (Exception ex)
            {
                returnForReload = false;
                morning.Clear();
                Monitor.Log($"无法重新读取当天存档。{ex}", LogLevel.Error);
            }
            return;
        }

        if (returnForReload && (Game1.gameMode == Game1.errorLogMode
            || (Game1.gameMode == Game1.titleScreenGameMode && !Game1.exitToTitle)))
        {
            returnForReload = false;
            morning.Clear();
        }

        if (Context.IsWorldReady && Context.IsMultiplayer)
        {
            if (!warnedMultiplayer)
                Monitor.Log("碰到什么就和什么结婚仅在单人存档中启用。", LogLevel.Info);
            warnedMultiplayer = true;
            return;
        }

        if (wakeUp is { } waking && (!ReferenceEquals(Game1.currentLocation, waking.Location)
            || !Game1.player.GetBoundingBox().Intersects(waking.Area)))
            wakeUp = null;

        if (needsCheckpoint && Context.IsPlayerFree && !returnForReload)
        {
            needsCheckpoint = false;
            try
            {
                morning.Prepare();
            }
            catch (Exception ex)
            {
                morning.Clear();
                Monitor.Log($"无法准备当天的重开检查点。{ex}", LogLevel.Error);
                Game1.addHUDMessage(new HUDMessage("婚礼挑战暂停：无法读取当天的重开检查点。"));
            }
        }
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (wedding.IsRunning)
        {
            if (wedding.Update())
                RestartDay();
            return;
        }

        if (!CanDetect)
        {
            pending = null;
            return;
        }

        if (pending is not { } partner)
            return;

        pending = null;
        try
        {
            wedding.Start(partner);
        }
        catch (Exception ex)
        {
            wedding.Clear();
            Monitor.Log($"无法开始婚礼。{ex}", LogLevel.Error);
            RestartDay();
        }
    }

    private void RestartDay()
    {
        if (!morning.IsReady)
            return;
        wedding.Clear();
        returnForReload = true;
        Game1.ExitToTitle(() => loadFromTitle = true);
    }

    private static void BeforePresent(GraphicsDevice __instance)
    {
        // Some title/loading frames bypass the normal rendered event.
        if ((instance.returnForReload || instance.wedding.IsEnding)
            && ReferenceEquals(__instance, Game1.graphics.GraphicsDevice))
            __instance.Clear(Color.Black);
    }

    private void ProtectWakeUp()
    {
        wakeUp = null;
        if (Context.IsMultiplayer || Game1.player.currentLocation is not { } location)
            return;

        Farmer farmer = Game1.player;
        Point tile = farmer.TilePoint;
        BedFurniture? bed = BedFurniture.GetBedAtTile(location, tile.X, tile.Y);
        // The vanilla wake-up position can be one tile beside the bed, facing it.
        if (bed is null && farmer.FacingDirection is Game1.left or Game1.right)
        {
            int offset = farmer.FacingDirection == Game1.right ? 1 : -1;
            bed = BedFurniture.GetBedAtTile(location, tile.X + offset, tile.Y);
        }

        if (bed is not null)
            wakeUp = (location, bed, Rectangle.Union(bed.GetBoundingBox(), farmer.GetBoundingBox()));
    }

    private static bool BeforeSleepTouch(GameLocation __instance, string[] __0, Vector2 __1) =>
        __0.Length == 0 || __0[0] != "Sleep"
        || instance.wakeUp is not { } waking || !ReferenceEquals(waking.Location, __instance)
        || !ReferenceEquals(waking.Bed, BedFurniture.GetBedAtTile(__instance, (int)__1.X, (int)__1.Y));

    private static bool BeforeMove(Farmer __instance, int __0, ref bool __result)
    {
        if (!ReferenceEquals(__instance, Game1.player) || !instance.CanDetect || instance.pending is not null)
            return true;

        var current = __instance.GetBoundingBox();
        var destination = __instance.nextPosition(__0);
        var location = __instance.currentLocation;
        if (current == destination || location.isCollidingWithWarp(destination, __instance) is not null)
            return true;

        instance.pending = Contact.FindOnMove(location, current, destination);
        if (instance.pending is null)
            return true;

        // End this movement before the queued wedding starts at the end of the tick.
        __result = true;
        return false;
    }

    private static void BeforeNpcCollision(
        GameLocation __instance, Rectangle position, bool isFarmer, Character? character,
        bool pathfinding, bool projectile, bool skipCollisionEffects)
    {
        if (isFarmer || pathfinding || projectile || skipCollisionEffects || character is not NPC npc
            || instance.pending is not null || !ReferenceEquals(__instance, Game1.currentLocation)
            || !ReferenceEquals(npc.currentLocation, __instance) || !instance.CanDetect
            || npc.IsInvisible || npc.EventActor || npc == Game1.player.mount)
            return;

        // A walking NPC can be stopped by the farmer before their hitboxes overlap.
        // Check the full body used for that movement and leave vanilla collision intact.
        Rectangle current = npc.GetBoundingBox();
        Rectangle farmer = Game1.player.GetBoundingBox();
        if (position.Width == current.Width && position.Height == current.Height
            && position.Intersects(farmer) && !current.Intersects(farmer))
            instance.pending = new Contact(npc);
    }
}
