using BepInEx;
using UnityEngine;
using SlugBase.Features;
using static SlugBase.Features.FeatureTypes;
using System.Collections.Generic;
using System.Linq;
using RWCustom;

namespace TheSandevistated
{
    [BepInPlugin(MOD_ID, "The Sandevistated", "0.1.0")]
    class Plugin : BaseUnityPlugin
    {
        private const string MOD_ID = "hazardous.sand";

        private static readonly Color color1 = new Color32(0xF5, 0x27, 0xE0, 255); // #F527E0
        private static readonly Color color2 = new Color32(0x27, 0xF5, 0xD3, 255); // #27F5D3
        private static readonly Color color3 = new Color32(0xE0, 0xFF, 0x00, 255); // #E0FF00

        private Color GetGradientColor(float time)
        {
            float cycleSpeed = 0.5f;

            float progress = (time * cycleSpeed) % 1.0f;

            float segmentLength = 1.0f / 3.0f;

            if (progress < segmentLength)
            {
                float t = progress / segmentLength;
                return Color.Lerp(color1, color2, t);
            }
            else if (progress < segmentLength * 2)
            {
                float t = (progress - segmentLength) / segmentLength;
                return Color.Lerp(color2, color3, t);
            }
            else
            {
                float t = (progress - (segmentLength * 2)) / segmentLength;
                return Color.Lerp(color3, color1, t);
            }
        }

        // timestop variables
        private bool timeStopped = false;
        private float stopTimer = 0f;
        private Dictionary<AbstractCreature, Vector2> frozen = new();

        // ghost trail variables
        private const float ghostFadeTime = 2f;
        private List<GhostClone> ghostClones = new();
        private const float ghostLifetime = 0.8f;
        private const float ghostInterval = 0.025f;
        private float ghostTimer = 0f;
        private bool wantsToSpawnGhost = false;

        private static bool isInit = false;

        // --- Class-level variables for shader and overlay ---
        private FShader greenShader;
        private FSprite linkOverlay;
        private FShader rainbowTrailShader;

        private class GhostClone
        {
            public FSprite[] sprites;
            public float timer;
            private FContainer _container;

            public GhostClone(PlayerGraphics pGraphics, RoomCamera.SpriteLeaser sLeaser, FContainer container, Color cloneColor)
            {
                this.timer = ghostLifetime; // Use the const from Plugin class
                this._container = container;

                this.sprites = new FSprite[sLeaser.sprites.Length];
                for (int i = 0; i < sLeaser.sprites.Length; i++)
                {
                    if (i == 9 || i == 10) continue;

                    FSprite oldSprite = sLeaser.sprites[i];

                    // Safety check, especially for mod compatibility
                    if (oldSprite == null || oldSprite.element == null) continue;

                    FSprite newSprite = new FSprite(oldSprite.element.name);

                    // Copy all visual properties from the player's current sprite
                    newSprite.SetPosition(oldSprite.GetPosition());
                    newSprite.rotation = oldSprite.rotation;
                    newSprite.scaleX = oldSprite.scaleX;
                    newSprite.scaleY = oldSprite.scaleY;
                    newSprite.color = oldSprite.color;

                    newSprite.color = cloneColor;

                    this.sprites[i] = newSprite;
                    this._container.AddChild(newSprite);
                }
            }

            // Called from DrawSprites to update opacity
            public void UpdateDraw(float maxLifetime)
            {
                float progress = (maxLifetime - timer) / maxLifetime;
                float currentAlpha = Mathf.Lerp(0.5f, 0f, progress);

                foreach (FSprite sprite in this.sprites.Where(s => s != null))
                {
                    sprite.alpha = currentAlpha;
                }
            }

            public void Destroy()
            {
                foreach (FSprite sprite in this.sprites.Where(s => s != null))
                {
                    sprite.RemoveFromContainer();
                }
            }
        }



        public void OnEnable()
        {
            On.Player.Update += Player_Update;
            On.RainWorld.OnModsInit += RainWorld_OnModsInit;
            On.PlayerGraphics.DrawSprites += PlayerGraphics_DrawSprites;
            On.Player.Jump += Player_Jump;

            // --- FIX: Hook RoomCamera.ctor to create the overlay ONCE ---
            On.RoomCamera.ctor += RoomCamera_ctor;
        }

        private void RoomCamera_ctor(On.RoomCamera.orig_ctor orig, RoomCamera self, RainWorldGame game, int cameraNumber)
        {
            orig(self, game, cameraNumber);

            // Create the overlay sprite
            FSprite overlay = new FSprite("Futile_White");
            overlay.scaleX = Futile.screen.pixelWidth / overlay.width;
            overlay.scaleY = Futile.screen.pixelHeight / overlay.height;
            overlay.color = new Color32(39,245,156,255);
            overlay.alpha = 0f; // Start invisible
            overlay.x = Futile.screen.pixelWidth / 2;
            overlay.y = Futile.screen.pixelHeight / 2;

            // Apply the shader *if* it was loaded correctly
            if (this.greenShader != null)
            {
                overlay.shader = this.greenShader;
            }

            // Add it to the HUD container to draw it on top of everything
            self.ReturnFContainer("HUD").AddChild(overlay);

            // Store this new overlay in our class variable
            this.linkOverlay = overlay;
        }

        private void Player_Jump(On.Player.orig_Jump orig, Player self)
        {
            orig(self);

            if (timeStopped)
            {
                self.jumpBoost *= 1.2f;
            }
        }

        private void PlayerGraphics_DrawSprites(On.PlayerGraphics.orig_DrawSprites orig, PlayerGraphics self, RoomCamera.SpriteLeaser sLeaser, RoomCamera rCam, float timeStacker, Vector2 camPos)
        {
            orig(self, sLeaser, rCam, timeStacker, camPos);


            if (self.player == null || self.player.room == null || self.player.room.abstractRoom != rCam.room.abstractRoom)
                return;
            

            // Spawn a new ghost if Player_Update requested it
            if (wantsToSpawnGhost)
            {
                Color ghostColor = GetGradientColor(Time.time);
                ghostClones.Add(new GhostClone(self, sLeaser, rCam.ReturnFContainer("Midground"), ghostColor));
                wantsToSpawnGhost = false; // Reset the flag
            }

            // Update all existing ghosts
            foreach (var ghost in ghostClones)
            {
                ghost.UpdateDraw(ghostLifetime);
            }
        }

        private void RainWorld_OnModsInit(On.RainWorld.orig_OnModsInit orig, RainWorld rainWorld)
        {
            orig(rainWorld);
            if (isInit) return;
            isInit = true;

            try
            {
                var assetBundle = AssetBundle.LoadFromFile(AssetManager.ResolveFilePath("AssetBundles/rainbowtrail"));
                rainWorld.Shaders.Add("hazardous.rainbowtrail", FShader.CreateShader(
                    "hazardous.rainbowtrail",
                    assetBundle.LoadAsset<Shader>("Assets/Shaders/rainbowTrail.shader")
                ));

                var greenShaderAsset = assetBundle.LoadAsset<Shader>("Assets/Shaders/greenTint.shader");

                // Create the shader instance
                FShader shaderInstance = FShader.CreateShader(
                    "hazardous.greentint",
                    greenShaderAsset
                );

                rainWorld.Shaders.Add("hazardous.greentint", shaderInstance);

                this.greenShader = shaderInstance;
            }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private void Player_Update(On.Player.orig_Update orig, Player self, bool eu)
        {
            orig(self, eu);
            if (self == null || self.inShortcut || self.dead || self.Stunned || self.room == null)
            {
                if (timeStopped)
                {
                    timeStopped = false;
                    frozen.Clear();
                }

                ClearAllGhosts(); 
                return;
            }

                if (self.input[0].spec && self.bodyMode == Player.BodyModeIndex.Crawl && !timeStopped)
            {
                timeStopped = true;
                stopTimer = 5f;
                frozen.Clear();
                ghostTimer = 0f;

                Vector2 pos = self.mainBodyChunk.pos;
                Color sparkColor = new Color32(39, 245, 156, 255); 
                int sparkCount = 6;

                self.room.PlaySound(SoundID.Zapper_Zap, pos);

                for (int i = 0; i < sparkCount; i++)
                {
                    self.room.AddObject(new NeuronSpark(pos));
                }

                foreach (var ac in self.room.abstractRoom.creatures)
                {
                    if (ac.realizedCreature != null && ac.realizedCreature != self)
                    {
                        frozen[ac] = ac.realizedCreature.mainBodyChunk.pos;
                        ac.realizedCreature.Stun(200);
                    }
                }
            }

            self.slugcatStats.runspeedFac = 1.2f;

            if (timeStopped)
            {
                stopTimer -= Time.deltaTime;
                self.slugcatStats.runspeedFac = 1.5f;

                // Maintain creature freeze
                foreach (var kvp in frozen)
                {
                    var c = kvp.Key;
                    if (c?.realizedCreature == null) continue;
                    var body = c.realizedCreature.mainBodyChunk;
                    body.vel = Vector2.zero;
                    body.pos = kvp.Value;
                }

                // --- Ghost Spawning Logic ---
                ghostTimer -= Time.deltaTime;
                if (ghostTimer <= 0f)
                {
                    ghostTimer = ghostInterval;
                    wantsToSpawnGhost = true; // Set flag for DrawSprites
                }

                // Check for timestop ending
                if (stopTimer <= 0f)
                {
                    frozen.Clear();
                    timeStopped = false;
                }
            }
            else
            {
                // Ensure we don't spawn a ghost right after it ends
                wantsToSpawnGhost = false;

            }

            if (linkOverlay != null)
            {
               
                float targetAlpha = timeStopped ? 0.25f : 0f;
                float fadeSpeed = 2.0f;

                linkOverlay.alpha = Mathf.MoveTowards(linkOverlay.alpha, targetAlpha, fadeSpeed * Time.deltaTime);
            }

            for (int i = ghostClones.Count - 1; i >= 0; i--)
            {
                var ghost = ghostClones[i];
                ghost.timer -= Time.deltaTime;

                if (ghost.timer <= 0f)
                {
                    ghost.Destroy();
                    ghostClones.RemoveAt(i);
                }
            }
        }

        private void ClearAllGhosts()
        {
            foreach (var ghost in ghostClones)
            {
                ghost.Destroy();
            }
            ghostClones.Clear();
            wantsToSpawnGhost = false;
        }
    }
}