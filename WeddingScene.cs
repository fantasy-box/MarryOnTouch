using System.Globalization;
using System.Text.RegularExpressions;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.GameData.Weddings;
using StardewValley.Menus;
using StardewValley.TokenizableStrings;

namespace MarryOnTouch;

internal sealed class WeddingScene
{
    private const string PartnerId = "MarryOnTouchPartner";
    private static WeddingScene instance = null!;
    private Event? ceremony;
    private Contact? partner;
    private GameLocation? destination;
    private bool finished;
    private DialogueBox? dialogue;
    private string? dialoguePage;
    private double dialogueSeconds;
    private Rectangle placementFrame;
    private Rectangle propBounds;
    private (Options Options, float Zoom)? previousZoom;

    public bool IsRunning => partner is not null;
    public bool IsEnding => finished || ceremony is { skipped: true };

    public void Install(Harmony harmony)
    {
        instance = this;
        harmony.Patch(
            AccessTools.Method(typeof(Event), "addActor"),
            prefix: new HarmonyMethod(typeof(WeddingScene), nameof(BeforeAddActor)));
        harmony.Patch(
            AccessTools.Method(typeof(GameLocation), nameof(GameLocation.checkForEvents)),
            prefix: new HarmonyMethod(typeof(WeddingScene), nameof(BeforeCheckForEvents)));
        harmony.Patch(
            AccessTools.Method(typeof(Event.DefaultCommands), nameof(Event.DefaultCommands.Viewport)),
            postfix: new HarmonyMethod(typeof(WeddingScene), nameof(AfterViewport)));
    }

    public void Start(Contact target)
    {
        partner = target;
        destination = Game1.getLocationFromName("Town") ?? throw new InvalidOperationException("Town is missing.");
        ceremony = CreateEvent(target);
        ceremony.onEventFinished += () => finished = true;

        if (ReferenceEquals(Game1.currentLocation, destination))
        {
            destination.startEvent(ceremony);
            return;
        }

        LocationRequest request = Game1.getLocationRequest("Town");
        request.OnWarp += () => destination?.startEvent(ceremony);
        Game1.warpFarmer(request, 28, 65, Game1.down);
    }

    public bool Update()
    {
        // The end callback runs after the game queues a warp, before it finishes.
        // Exiting to title at that point can leave isWarping set in the next load.
        if (finished || !ReferenceEquals(Game1.CurrentEvent, ceremony))
            return Game1.locationRequest is null && !Game1.isWarping;

        if (!placementFrame.IsEmpty && ceremony is { skipped: false })
            FramePlacement();

        // Let the ceremony run unattended, including the vanilla dialogue pages.
        if (Game1.activeClickableMenu is not DialogueBox box || box.isQuestion)
        {
            dialogue = null;
            return false;
        }

        string page = box.getCurrentString();
        if (dialogue != box || dialoguePage != page)
        {
            dialogue = box;
            dialoguePage = page;
            dialogueSeconds = 0;
        }
        dialogueSeconds += Game1.currentGameTime.ElapsedGameTime.TotalSeconds;
        if (dialogueSeconds >= 2.5)
        {
            box.receiveLeftClick(0, 0);
            dialogueSeconds = 0;
        }

        return false;
    }

    public void Clear()
    {
        ceremony = null;
        partner = null;
        destination = null;
        finished = false;
        dialogue = null;
        dialoguePage = null;
        placementFrame = Rectangle.Empty;
        propBounds = Rectangle.Empty;
        if (previousZoom is { } saved)
            saved.Options.desiredBaseZoomLevel = saved.Zoom;
        previousZoom = null;
    }

    private static Event CreateEvent(Contact target)
    {
        WeddingData data = DataLoader.Weddings(Game1.content);
        string script = data.EventScript["default"];
        Farmer farmer = Game1.player;
        NPC? npcPartner = target.Subject as NPC;
        string? npcName = npcPartner?.Name;
        Gender partnerGender = npcPartner?.Gender ?? Gender.Undefined;
        bool partnerIsMale = partnerGender == Gender.Male
            || (partnerGender != Gender.Female && farmer.IsMale);
        string partnerName = target.DisplayName.Replace('/', '／').Replace('"', '”')
            .Replace('[', '［').Replace(']', '］');
        var attendees = (data.Attendees?.Values.AsEnumerable() ?? Enumerable.Empty<WeddingAttendeeData>())
            .Where(attendee => attendee.Id != npcName
                && GameStateQuery.CheckConditions(attendee.Condition, null, farmer)
                && (attendee.IgnoreUnlockConditions || !NPC.TryGetData(attendee.Id, out var npcData)
                    || GameStateQuery.CheckConditions(npcData.UnlockConditions, null, farmer)))
            .ToArray();

        string expanded = TokenParser.ParseText(script, customParser: ResolveWeddingToken, player: farmer)
            .Replace(Dialogue.spouseSpecialCharacter, partnerName, StringComparison.Ordinal);
        var weddingEvent = new Event(expanded, null, "-2", farmer)
        {
            markEventSeen = false,
            skippable = true
        };
        for (int index = 3; index < weddingEvent.eventCommands.Length; index++)
        {
            string command = weddingEvent.eventCommands[index].Trim();
            if (string.Equals(command, "end wedding", StringComparison.OrdinalIgnoreCase))
                weddingEvent.eventCommands[index] = "end";
            else if (partnerGender == Gender.Male && command.StartsWith("showFrame " + PartnerId + " ", StringComparison.OrdinalIgnoreCase))
            {
                string[] args = ArgUtility.SplitBySpace(command);
                if (int.TryParse(args[2], out int frame) && frame is >= 36 and <= 38)
                {
                    // Vanilla applies this offset only to the reserved actor name "spouse".
                    args[2] = (frame + 12).ToString(CultureInfo.InvariantCulture);
                    weddingEvent.eventCommands[index] = string.Join(" ", args);
                }
            }
        }
        return weddingEvent;

        bool ResolveWeddingToken(string[] query, out string replacement, Random random, Farmer player)
        {
            replacement = query[0].ToLowerInvariant() switch
            {
                "spouseactor" => PartnerId,
                "spousename" => partnerName,
                // Resolve the vanilla NPC branch against the scene actor, regardless of the save's marriage status.
                "spousefarmertext" when query.Length >= 3 => Regex.Replace(query[2], @"(?<!%)\bspouse\b", PartnerId),
                "spousegenderedtext" when query.Length >= 3 => query[partnerIsMale ? 1 : 2],
                "setupcontextualweddingattendees" => string.Concat(attendees.Select(attendee => " " + attendee.Setup)),
                "contextualweddingcelebrations" => string.Concat(attendees
                    .Where(attendee => !string.IsNullOrEmpty(attendee.Celebration))
                    .Select(attendee => attendee.Celebration + "/")),
                _ => null!
            };
            return replacement is not null;
        }
    }

    private static bool BeforeCheckForEvents(GameLocation __instance) =>
        !ReferenceEquals(__instance, instance.destination);

    private static void AfterViewport(Event __0, string[] __1)
    {
        if (!ReferenceEquals(__0, instance.ceremony))
            return;

        // The vanilla script moves the camera offstage at the end of the ceremony.
        instance.placementFrame = Rectangle.Empty;
        instance.propBounds = Rectangle.Empty;
        if (__1.Length < 3 || !int.TryParse(__1[1], out int x) || x < 0
            || !int.TryParse(__1[2], out int y) || y < 0)
            return;

        PartnerActor? actor = __0.actors.OfType<PartnerActor>().FirstOrDefault();
        if (actor is null)
            return;
        Rectangle placement = actor.PlacementBounds;
        if (placement.IsEmpty)
            return;

        Rectangle bounds = IncludeCharacter(placement, __0.farmer);
        foreach (NPC guest in __0.actors)
        {
            if (guest != actor && guest.Position.X >= 0 && guest.Position.Y >= 0)
                bounds = IncludeCharacter(bounds, guest);
        }
        bounds.Inflate(16, 16);
        instance.propBounds = placement;
        instance.placementFrame = bounds;
        instance.FramePlacement();

        static Rectangle IncludeCharacter(Rectangle frame, Character character)
        {
            Rectangle sprite = character.GetBoundingBox();
            sprite.Y -= 64;
            sprite.Height += 64;
            return Rectangle.Union(frame, sprite);
        }
    }

    private void FramePlacement()
    {
        // Draw the vanilla arch behind the prop when its top would cover the prop.
        foreach (TemporaryAnimatedSprite sprite in Game1.currentLocation.TemporarySprites)
        {
            if (sprite.layerDepth == 1f && sprite.sourceRect == new Rectangle(540, 1196, 98, 54)
                && propBounds.Intersects(new Rectangle((int)sprite.Position.X, (int)sprite.Position.Y, 98 * 4, 54 * 4)))
                sprite.layerDepth = 0f;
        }

        int width = Game1.viewport.Width;
        int height = Game1.viewport.Height;
        if (width <= 0 || height <= 0)
            return;

        var buffer = Game1.graphics.GraphicsDevice.PresentationParameters;
        float zoomToFit = Math.Min(buffer.BackBufferWidth / (float)placementFrame.Width,
            buffer.BackBufferHeight / (float)placementFrame.Height);
        Options options = Game1.options;
        if (zoomToFit > 0f && zoomToFit < options.desiredBaseZoomLevel)
        {
            previousZoom ??= (options, options.desiredBaseZoomLevel);
            options.desiredBaseZoomLevel = zoomToFit * 0.98f;
        }

        Game1.viewport.X = FitAxis(Game1.viewport.X, width, placementFrame.Left, placementFrame.Right);
        Game1.viewport.Y = FitAxis(Game1.viewport.Y, height, placementFrame.Top, placementFrame.Bottom);

        static int FitAxis(int current, int size, int start, int end) =>
            end - start <= size ? Math.Clamp(current, end - size, start) : (start + end - size) / 2;
    }

    private static bool BeforeAddActor(Event __instance, string __0, int __1, int __2, int __3, GameLocation __4)
    {
        if (!ReferenceEquals(__instance, instance.ceremony) || __0 != PartnerId || instance.partner is not { } target)
            return true;

        var position = new Vector2(__1 * 64, __2 * 64);
        if (__instance.actors.FirstOrDefault(actor => actor.Name == PartnerId) is NPC existing)
        {
            existing.Position = position;
            existing.faceDirection(__3);
        }
        else
        {
            var actor = new PartnerActor(target, position, __3) { currentLocation = __4 };
            __instance.actors.Add(actor);
        }
        return false;
    }

    private sealed class PartnerActor : NPC
    {
        private readonly Contact target;
        private readonly bool useNpcSprite;

        public PartnerActor(Contact target, Vector2 position, int direction)
            : base(CreateSprite(target), position, direction, PartnerId)
        {
            this.target = target.ForWedding();
            useNpcSprite = target.Subject is NPC { IsVillager: true };
            displayName = target.DisplayName;
            EventActor = true;
            AllowDynamicAppearance = false;
        }

        public Rectangle PlacementBounds => target.GetPlacementBounds(Position);

        private static AnimatedSprite CreateSprite(Contact target) =>
            target.Subject is NPC { IsVillager: true } npc
                ? npc.Sprite.Clone()
                : new AnimatedSprite("Characters\\Lewis", 0, 16, 32);

        public override void draw(SpriteBatch b, float alpha = 1)
        {
            if (useNpcSprite && Sprite.Texture is { } texture && texture.Bounds.Contains(Sprite.SourceRect))
            {
                base.draw(b, alpha);
                return;
            }

            target.Draw(b, Position, Math.Max(0, StandingPixel.Y / 10000f), alpha);
        }
    }
}
