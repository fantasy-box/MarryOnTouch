using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;

namespace MarryOnTouch;

internal sealed record Contact(object Subject, bool IsPlaced = false)
{
    private sealed record GarbageCan(int Season);

    public static Contact? FindOnMove(GameLocation location, Rectangle current, Rectangle destination)
    {
        // Objects already overlapping the farmer can't start a wedding until re-entered.
        bool Entered(Rectangle target) => destination.Intersects(target) && !current.Intersects(target);

        foreach (NPC npc in location.characters)
            if (!npc.IsInvisible && npc != Game1.player.mount && Entered(npc.GetBoundingBox()))
                return new Contact(npc);

        foreach (FarmAnimal animal in location.animals.Values)
            if (Entered(animal.GetBoundingBox()))
                return new Contact(animal);

        foreach (var furniture in location.furniture)
            if (furniture is not BedFurniture && Entered(furniture.GetBoundingBox()))
                return new Contact(furniture);

        foreach (var feature in location.largeTerrainFeatures)
            if (Entered(feature.getBoundingBox()))
                return new Contact(feature);

        foreach (var clump in location.resourceClumps)
            if (Entered(clump.getBoundingBox()))
                return new Contact(clump);

        foreach (Debris debris in location.debris)
        {
            if (debris.item is BedFurniture || (debris.item is null && string.IsNullOrEmpty(debris.itemId.Value)))
                continue;
            foreach (Chunk chunk in debris.Chunks)
            {
                Vector2 position = chunk.position.Value;
                if (Entered(new Rectangle((int)position.X, (int)position.Y, 32, 32)))
                {
                    Item item = debris.item ?? ItemRegistry.Create(debris.itemId.Value);
                    if (item is not BedFurniture)
                        return new Contact(item);
                    break;
                }
            }
        }

        for (int y = Math.Max(0, destination.Top / 64); y <= (destination.Bottom - 1) / 64; y++)
        {
            for (int x = Math.Max(0, destination.Left / 64); x <= (destination.Right - 1) / 64; x++)
            {
                var tile = new Vector2(x, y);
                if (location.Objects.TryGetValue(tile, out var obj) && obj is not BedFurniture && Entered(obj.GetBoundingBox()))
                    return new Contact(obj, IsPlaced: true);
                if (location.terrainFeatures.TryGetValue(tile, out var feature)
                    && feature is not HoeDirt { crop: null } && Entered(feature.getBoundingBox()))
                    return new Contact(feature);

                if (Entered(new Rectangle(x * 64, y * 64, 64, 64))
                    && location.doesTileHaveProperty(x, y, "Action", "Buildings")?.StartsWith("Garbage ", StringComparison.Ordinal) == true)
                    return new Contact(new GarbageCan(location.GetSeasonIndex()));
            }
        }
        return null;
    }

    public string DisplayName => Subject switch
    {
        NPC npc => npc.displayName,
        FarmAnimal animal => animal.displayName,
        Chest { giftbox.Value: true, giftboxIsStarterGift.Value: true } => "开局礼物盒",
        Chest { giftbox.Value: true } => "礼物盒",
        Item item => item.DisplayName,
        GarbageCan => "垃圾桶",
        HoeDirt { crop: not null } dirt => ItemRegistry.Create("(O)" + dirt.crop.indexOfHarvest.Value).DisplayName,
        FruitTree => "果树",
        Tree => "树",
        Bush => "灌木",
        Grass => "草丛",
        ResourceClump => "大型障碍物",
        _ => Subject.GetType().Name
    };

    public Contact ForWedding()
    {
        // Large terrain features store their tile in networked state. Draw a separate copy.
        if (Subject is Bush original)
        {
            var bush = new Bush { Tile = original.Tile };
            bush.size.Value = original.size.Value;
            bush.flipped.Value = original.flipped.Value;
            bush.townBush.Value = original.townBush.Value;
            bush.drawShadow.Value = original.drawShadow.Value;
            bush.sourceRect.Value = original.sourceRect.Value;
            return new Contact(bush);
        }

        return Subject switch
        {
            GiantCrop crop => new Contact(new GiantCrop(crop.Id, crop.Tile)),
            ResourceClump clump => new Contact(new ResourceClump(clump.parentSheetIndex.Value,
                clump.width.Value, clump.height.Value, clump.Tile, textureName: clump.textureName.Value)),
            _ => this
        };
    }

    public Rectangle GetPlacementBounds(Vector2 position)
    {
        switch (Subject)
        {
            case Furniture furniture:
                Rectangle source = furniture.sourceRect.Value;
                return new Rectangle((int)position.X, (int)position.Y + 64 - source.Height * 4,
                    source.Width * 4, source.Height * 4);
            case Tree or FruitTree or Bush or ResourceClump:
                var feature = (TerrainFeature)Subject;
                Rectangle bounds = feature.getRenderBounds();
                bounds.Offset((GetPlacementPosition(position) - feature.Tile * 64f).ToPoint());
                if (feature is GiantCrop)
                {
                    bounds.Y -= 64;
                    bounds.Height += 64;
                }
                return bounds;
            default:
                return Rectangle.Empty;
        }
    }

    private Vector2 GetPlacementPosition(Vector2 position) => Subject switch
    {
        ResourceClump clump => position - new Vector2(0, (clump.height.Value - 1) * 64),
        _ => position
    };

    public void Draw(SpriteBatch batch, Vector2 position, float depth, float alpha)
    {
        Vector2 feet = Game1.GlobalToLocal(Game1.viewport, position + new Vector2(32, 56));
        Vector2 tile = GetPlacementPosition(position) / 64f;
        Color color = Color.White * alpha;
        switch (Subject)
        {
            case Chest { giftbox.Value: true } gift:
                // Gift boxes use a world sprite instead of a registered inventory icon.
                DrawSprite(Game1.giftboxTexture, new Rectangle(0, gift.giftboxIndex.Value * 32, 16, 32), 4f);
                break;
            case Furniture furniture:
                Rectangle bounds = GetPlacementBounds(position);
                furniture.drawAtNonTileSpot(batch, Game1.GlobalToLocal(Game1.viewport,
                    new Vector2(bounds.X, bounds.Y)), depth, alpha);
                break;
            case StardewValley.Object obj when IsPlaced:
                obj.draw(batch, (int)tile.X, (int)tile.Y, alpha);
                break;
            case Item item:
                DrawItem(item);
                break;
            case Character character when character.Sprite.Texture is { } texture:
                Rectangle frame = character.Sprite.SourceRect;
                DrawSprite(texture, frame, Math.Min(4f, 88f / Math.Max(frame.Width, frame.Height)));
                break;
            case GarbageCan can:
                Texture2D cans = Game1.content.Load<Texture2D>("LooseSprites\\Cursors2");
                int x = 22 + can.Season * 17;
                DrawSprite(cans, new Rectangle(x, 11, 16, 16), 4f);
                DrawSprite(cans, new Rectangle(x, 0, 16, 10), 4f, -44f, depth + 0.0001f);
                break;
            case HoeDirt { crop: not null } dirt:
                dirt.crop.drawWithOffset(batch, tile, color, 0f, Vector2.Zero);
                break;
            case Tree or FruitTree or Bush or Grass or ResourceClump:
                var terrain = (TerrainFeature)Subject;
                Vector2 originalTile = terrain.Tile;
                try
                {
                    terrain.Tile = tile;
                    terrain.draw(batch);
                }
                finally
                {
                    terrain.Tile = originalTile;
                }
                break;
            case TerrainFeature feature:
                feature.drawInMenu(batch, feet - new Vector2(16, 0), feature.Tile, 1.6f, depth);
                break;
        }

        void DrawItem(Item item)
        {
            item.drawInMenu(batch, feet - new Vector2(32, 52), 0.9f, alpha, depth,
                StackDrawType.Hide, Color.White, false);
        }

        void DrawSprite(Texture2D texture, Rectangle source, float scale, float offsetY = 0f, float? layer = null)
        {
            batch.Draw(texture, feet + new Vector2(0, offsetY), source, color, 0,
                new Vector2(source.Width / 2f, source.Height), scale, SpriteEffects.None, layer ?? depth);
        }
    }
}
