using System.Collections.Generic;
using JetBrains.Annotations;
using Microsoft.Xna.Framework;
using Netcode;
using StardewValley;
using StardewValley.Locations;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using xTile.Dimensions;

namespace AggressiveAcorns {

    internal class AggressiveTree : Tree {

        private GameLocation _location;
        private Vector2 _position;
        private bool _skipUpdate;
        private readonly IModConfig _config = AggressiveAcorns.Config;


        [UsedImplicitly]
        public AggressiveTree() { }


        public AggressiveTree([NotNull] Tree tree) {
            growthStage.Value = tree.growthStage.Value;
            treeType.Value = tree.treeType.Value;
            health.Value = tree.health.Value;
            flipped.Value = tree.flipped.Value;
            stump.Value = tree.stump.Value;
            tapped.Value = tree.tapped.Value;
        }


        private AggressiveTree(int treeType, int growthStage, bool skipFirstUpdate = false)
            : base(treeType, growthStage) {
            _skipUpdate = skipFirstUpdate;
        }


        [NotNull]
        public Tree ToTree() {
            var tree = new Tree();
            tree.growthStage.Value = growthStage.Value;
            tree.treeType.Value = treeType.Value;
            tree.health.Value = health.Value;
            tree.flipped.Value = flipped.Value;
            tree.stump.Value = stump.Value;
            tree.tapped.Value = tapped.Value;

            SyncFieldToTree<NetBool, bool>(tree, "destroy");

            return tree;
        }


        public override bool isPassable([CanBeNull] Character c = null) {
            return health.Value <= -99 || growthStage.Value <= _config.MaxPassibleGrowthStage;
        }


        public override void dayUpdate(GameLocation environment, Vector2 tileLocation) {
            _location = environment;
            _position = tileLocation;

            if (health.Value <= -100) {
                SetField<NetBool, bool>("destroy", true);
            } else if (!_skipUpdate && TreeCanGrow()) {
                TrySpread();
                TryIncreaseStage();
                ManageHibernation();
                TryRegrow();
                PopulateSeed();
            } else {
                _skipUpdate = false;
            }
        }


        public override bool performToolAction(Tool t, int explosion, Vector2 tileLocation, GameLocation location) {
            var prevent = _config.PreventScythe && t is MeleeWeapon;
            return !prevent && base.performToolAction(t, explosion, tileLocation, location);
        }


        // ===========================================================================================================


        private void SetField<TNetField, T>(string name, T value) where TNetField : NetField<T, TNetField> {
            AggressiveAcorns.ReflectionHelper.GetField<TNetField>(this, name).GetValue().Value = value;
        }


        private void SyncField<TNetField, T>(object origin, object target, string name)
            where TNetField : NetField<T, TNetField> {
            var value = AggressiveAcorns.ReflectionHelper.GetField<TNetField>(origin, name).GetValue().Value;
            AggressiveAcorns.ReflectionHelper.GetField<TNetField>(target, name).GetValue().Value = value;
        }


        private void SyncFieldToTree<TNetField, T>(Tree tree, string name) where TNetField : NetField<T, TNetField> {
            SyncField<TNetField, T>(this, tree, name);
        }


        // ===========================================================================================================

        #region Day_Update_Code

        private void TryIncreaseStage() {
            if (growthStage.Value >= treeStage ||
                (growthStage.Value >= _config.MaxShadedGrowthStage && IsShaded())) return;

            if (ExperiencingWinter()
                && (!_config.DoGrowInWinter ||
                    (treeType.Value == mushroomTree && _config.DoMushroomTreesHibernate))
            ) return;

            if (_config.DoGrowInstantly) {
                growthStage.Value = treeStage;
            } else if (Game1.random.NextDouble() < _config.DailyGrowthChance) {
                growthStage.Value += 1;
            }
        }


        private void ManageHibernation() {
            if (treeType.Value != mushroomTree
                || !_config.DoMushroomTreesHibernate
                || !ExperiencesWinter()) return;

            if (Game1.currentSeason.Equals("winter")) {
                stump.Value = true;
                health.Value = 5;
            } else if (Game1.currentSeason.Equals("spring") && Game1.dayOfMonth <= 1) {
                RegrowStumpIfNotShaded();
            }
        }


        private void TryRegrow() {
            if (treeType.Value == mushroomTree &&
                _config.DoMushroomTreesRegrow &&
                stump.Value &&
                (!ExperiencingWinter() || (!_config.DoMushroomTreesHibernate && _config.DoGrowInWinter)) &&
                (_config.DoGrowInstantly || Game1.random.NextDouble() < _config.DailyGrowthChance / 2)) {
                RegrowStumpIfNotShaded();
            }
        }


        private void RegrowStumpIfNotShaded() {
            if (IsShaded()) return;

            stump.Value = false;
            health.Value = startingHealth;

            /*  Not currently needed as AggressiveTree is converted to Tree and back around save to allow
             *  serialization (ie. new objects created so rotation is reset).
             *  If this changes (ie. Aggressive Tree cached over save or otherwise reused), must re-enable below code.
             */
            // AggressiveAcorns.ReflectionHelper.GetField<float>(this, "shakeRotation").SetValue(0);
        }


        private void TrySpread() {
            if (!(_location is Farm) ||
                growthStage.Value < treeStage ||
                (Game1.currentSeason.Equals("winter") && !_config.DoSpreadInWinter) ||
                (tapped.Value && !_config.DoTappedSpread) ||
                stump.Value) return;

            foreach (var seedPos in GetSpreadLocations()) {
                var tileX = (int) seedPos.X;
                var tileY = (int) seedPos.Y;
                if (_config.SeedsReplaceGrass && _location.terrainFeatures.TryGetValue(seedPos, out var feature) &&
                    feature is Grass) {
                    _location.terrainFeatures[seedPos] = new Tree(treeType.Value, 0);
                    hasSeed.Value = false;
                } else if (_location.isTileLocationOpen(new Location(tileX * 64, tileY * 64))
                           && !_location.isTileOccupied(seedPos)
                           && _location.doesTileHaveProperty(tileX, tileY, "Water", "Back") == null
                           && _location.isTileOnMap(seedPos)) {
                    _location.terrainFeatures.Add(seedPos, BuildOffspring());
                    hasSeed.Value = false;
                }
            }
        }


        [NotNull]
        private Tree BuildOffspring() {
            var tree = new AggressiveTree(treeType.Value, 0, true);
            return tree;
        }


        private IEnumerable<Vector2> GetSpreadLocations() {
            // pick random tile within +-3 x/y.
            if (Game1.random.NextDouble() < _config.DailySpreadChance) {
                var tileX = Game1.random.Next(-3, 4) + (int) _position.X;
                var tileY = Game1.random.Next(-3, 4) + (int) _position.Y;
                var seedPos = new Vector2(tileX, tileY);
                yield return seedPos;
            }
        }


        private void PopulateSeed() {
            if (growthStage.Value < treeStage || stump.Value) return;

            if (!_config.DoSeedsPersist) {
                hasSeed.Value = false;
            }

            if (Game1.random.NextDouble() < _config.DailySeedChance) {
                hasSeed.Value = true;
            }
        }


        private bool TreeCanGrow() {
            var prop = _location.doesTileHaveProperty((int) _position.X, (int) _position.Y, "NoSpawn", "Back");
            var tileCanSpawnTree = prop == null || !(prop.Equals("All") || prop.Equals("Tree") || prop.Equals("True"));
            var isBlockedSeed = growthStage.Value == 0 && _location.objects.ContainsKey(_position);
            return tileCanSpawnTree && !isBlockedSeed;
        }


        private bool ExperiencingWinter() {
            return Game1.currentSeason.Equals("winter") && ExperiencesWinter();
        }


        private bool ExperiencesWinter() {
            return _location.IsOutdoors && !(_location is Desert);
        }


        private bool IsShaded() {
            foreach (var adjacentTile in Utility.getSurroundingTileLocationsArray(_position)) {
                if (_location.terrainFeatures.TryGetValue(adjacentTile, out var feature)
                    && feature is Tree adjTree
                    && adjTree.growthStage.Value >= treeStage
                    && !adjTree.stump.Value) {
                    return true;
                }
            }

            return false;
        }

        #endregion

    }

}
