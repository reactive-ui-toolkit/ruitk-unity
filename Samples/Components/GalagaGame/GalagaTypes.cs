using System.Collections.Generic;
using UnityEngine;

namespace Ruitk.Samples.Components.GalagaGame {
  public enum GalagaScreen { MainMenu, Game }

  public static class GalagaTypes {
    // ───── Enums ─────

    public enum EnemyType { Zako, Goei, Commander }
    public enum EnemyPhase { Waiting, Entering, Formation, Diving, TractorBeam, Captured, Dead }
    public enum GamePhase { WaveIntro, Entering, Battle, WaveCleared, GameOver, Victory }
    public enum BulletOwner { Player, Enemy }

    // ───── Structs ─────

    public struct Vec2 {
      public float X;
      public float Y;
      public Vec2(float x, float y) { X = x; Y = y; }
      public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
      public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
      public static Vec2 operator *(Vec2 a, float s) => new Vec2(a.X * s, a.Y * s);
      public float Length() => Mathf.Sqrt(X * X + Y * Y);
      public Vec2 Normalized() { float l = Length(); return l > 0 ? new Vec2(X / l, Y / l) : new Vec2(0, 0); }
    }

    public struct PlayerState {
      public float X;
      public float Y;
      public bool Alive;
      public bool Captured;
      public bool InBeam;
      public float InvincibleTimer;
      public float ShootCooldown;
      public bool DualShip;
      public float DualShipX;
    }

    public struct EnemyState {
      public int Id;
      public EnemyType Type;
      public EnemyPhase Phase;
      public float X;
      public float Y;
      public int Health;
      public int FormationCol;
      public int FormationRow;
      public float EnterT;
      public float EnterDelay;
      public Vec2[] EnterPath;
      public float DiveT;
      public Vec2[] DivePath;
      public float AnimTimer;
      public int AnimFrame;
      public float ShootCooldown;
      public bool HasCapturedShip;
      public float TractorBeamTimer;
      public int TractorBeamFrame;
      public int TractorPhase; // 0=diving down, 1=beaming, 2=returning to formation
    }

    public struct BulletState {
      public int Id;
      public float X;
      public float Y;
      public float Vx;
      public float Vy;
      public BulletOwner Owner;
      public bool Active;
    }

    public struct ExplosionState {
      public int Id;
      public float X;
      public float Y;
      public float Timer;
      public int Frame;
      public bool IsPlayer;
    }

    public struct GameState {
      public GamePhase Phase;
      public PlayerState Player;
      public List<EnemyState> Enemies;
      public List<BulletState> Bullets;
      public List<ExplosionState> Explosions;
      public int Score;
      public int Lives;
      public int Wave;
      public float PhaseTimer;
      public float FormationSwayX;
      public float FormationSwayTimer;
      public float DiveTimer;
      public int NextEnemyId;
      public int NextBulletId;
      public int NextExplosionId;
      public bool TractorBeamActive;
      public int TractorBeamCommanderId;
    }

    // ───── Constants ─────

    public static class C {
      public const int VIEWPORT_W = 450;
      public const int VIEWPORT_H = 700;
      public const int SPRITE_SCALE = 2;
      public const int ENTITY_SIZE = 16 * SPRITE_SCALE; // 32
      public const int EXPLOSION_SIZE = 32 * SPRITE_SCALE; // 64

      public const float PLAYER_SPEED = 175f;
      public const float PLAYER_BULLET_SPEED = 450f;
      public const float ENEMY_BULLET_SPEED = 180f;
      public const float SHOOT_COOLDOWN = 0.3f;
      public const int MAX_PLAYER_BULLETS = 2;
      public const float INVINCIBLE_DURATION = 2f;

      public const float ENTER_SPEED = 0.532f;
      public const float DIVE_SPEED = 0.255f;
      public const float FORMATION_SWAY_SPEED = 0.5f;
      public const float FORMATION_SWAY_AMOUNT = 20f;
      public const float DIVE_INTERVAL_MIN = 2f;
      public const float DIVE_INTERVAL_MAX = 4f;
      public const float TRACTOR_BEAM_DURATION = 7f;

      public const float EXPLOSION_DURATION = 0.4f;
      public const float WAVE_INTRO_DURATION = 1f;
      public const float WAVE_CLEARED_DURATION = 3f;
      public const float ENTRY_WAVE_DELAY = 5f;

      // Formation grid origin
      public const float FORMATION_TOP = 60f;
      public const float FORMATION_LEFT = 55f;
      public const float FORMATION_COL_SPACING = 36f;
      public const float FORMATION_ROW_SPACING = 36f;

      // Scoring
      public const int SCORE_ZAKO_FORMATION = 50;
      public const int SCORE_ZAKO_DIVING = 100;
      public const int SCORE_GOEI_FORMATION = 80;
      public const int SCORE_GOEI_DIVING = 160;
      public const int SCORE_COMMANDER_FORMATION = 150;
      public const int SCORE_COMMANDER_DIVING = 400;

      public static int GetScore(EnemyType type, bool diving) {
        switch (type) {
          case EnemyType.Zako: return diving ? SCORE_ZAKO_DIVING : SCORE_ZAKO_FORMATION;
          case EnemyType.Goei: return diving ? SCORE_GOEI_DIVING : SCORE_GOEI_FORMATION;
          case EnemyType.Commander: return diving ? SCORE_COMMANDER_DIVING : SCORE_COMMANDER_FORMATION;
          default: return 50;
        }
      }
    }

    // ───── Formation layout ─────
    // 5 rows: row 0 = 4 commanders, rows 1-2 = 8 goei each, rows 3-4 = 10 zako each

    public static class Formation {
      public static List<EnemyState> CreateWave(int wave, ref int nextId) {
        var enemies = new List<EnemyState>(40);
        // Commanders: row 0, cols 3-6 (centered in 10-col grid)
        for (int col = 3; col <= 6; col++) {
          enemies.Add(MakeEnemy(ref nextId, EnemyType.Commander, 0, col, wave));
        }
        // Goei: rows 1-2, cols 1-8
        for (int row = 1; row <= 2; row++) {
          for (int col = 1; col <= 8; col++) {
            enemies.Add(MakeEnemy(ref nextId, EnemyType.Goei, row, col, wave));
          }
        }
        // Zako: rows 3-4, cols 0-9
        for (int row = 3; row <= 4; row++) {
          for (int col = 0; col <= 9; col++) {
            enemies.Add(MakeEnemy(ref nextId, EnemyType.Zako, row, col, wave));
          }
        }
        return enemies;
      }

      static EnemyState MakeEnemy(ref int nextId, EnemyType type, int row, int col, int wave) {
        int health = type == EnemyType.Commander ? 2 : 1;
        return new EnemyState {
          Id = nextId++,
          Type = type,
          Phase = EnemyPhase.Waiting,
          Health = health,
          FormationRow = row,
          FormationCol = col,
          EnterPath = new Vec2[0],
          DivePath = new Vec2[0],
        };
      }

      public static Vec2 GetFormationPos(int row, int col) {
        return new Vec2(
        C.FORMATION_LEFT + col * C.FORMATION_COL_SPACING,
        C.FORMATION_TOP + row * C.FORMATION_ROW_SPACING
        );
      }
    }

    // ───── Path helpers ─────

    public static class Paths {
      static Vec2 CubicBezier(Vec2 p0, Vec2 p1, Vec2 p2, Vec2 p3, float t) {
        float u = 1 - t;
        float uu = u * u;
        float uuu = uu * u;
        float tt = t * t;
        float ttt = tt * t;
        return new Vec2(
        uuu * p0.X + 3 * uu * t * p1.X + 3 * u * tt * p2.X + ttt * p3.X,
        uuu * p0.Y + 3 * uu * t * p1.Y + 3 * u * tt * p2.Y + ttt * p3.Y
        );
      }

      public static Vec2 EvalPath(Vec2[] path, float t) {
        if (path.Length < 4) return path.Length > 0 ? path[0] : new Vec2(0, 0);
        int segments = (path.Length - 1) / 3;
        float st = t * segments;
        int seg = Mathf.Min((int)st, segments - 1);
        float lt = st - seg;
        int i = seg * 3;
        return CubicBezier(path[i], path[i + 1], path[i + 2], path[i + 3], lt);
      }

      // All ships in a group share the same flight pattern (train formation).
      // Only the final segment curves each ship to its own formation position.

      public static Vec2[] MakeEntryPath(int row, int col, int enemyIndex, float swayX) {
        Vec2 target = Formation.GetFormationPos(row, col) + new Vec2(swayX, 0);
        int group = enemyIndex / 4;
        bool fromRight = group % 2 == 0;
        float centerX = C.VIEWPORT_W * 0.5f;

        // All ships in group share the same p0-p6 (pattern), p7-p9 are per-ship
        float edgeX = fromRight ? C.VIEWPORT_W + C.ENTITY_SIZE * 2 : -C.ENTITY_SIZE * 2;
        float enterY = 250f + (group % 3) * 40f;

        Vec2 p0 = new Vec2(edgeX, enterY);

        // Segment 1: Fly across screen, arcing down
        float crossX = fromRight ? centerX - 60 : centerX + 60;
        Vec2 p1 = new Vec2(fromRight ? C.VIEWPORT_W * 0.6f : C.VIEWPORT_W * 0.4f, enterY + 20);
        Vec2 p2 = new Vec2(crossX, enterY + 80);
        Vec2 p3 = new Vec2(crossX, enterY + 140);

        // Segment 2: Sweeping loop
        float loopDir = fromRight ? -1f : 1f;
        Vec2 p4 = new Vec2(crossX + loopDir * 60, enterY + 200);
        Vec2 p5 = new Vec2(crossX + loopDir * 20, enterY + 120);
        Vec2 p6 = new Vec2(crossX - loopDir * 20, enterY + 80);

        // Segment 3: Peel off to individual formation position
        Vec2 p7 = new Vec2((p6.X + target.X) * 0.5f, (p6.Y + target.Y) * 0.5f);
        Vec2 p8 = new Vec2(target.X, target.Y - 15);
        Vec2 p9 = target;

        return new Vec2[] { p0, p1, p2, p3, p4, p5, p6, p7, p8, p9 };
      }

      // Dive paths for different enemy types:
      // Zako: swoop down then loop back up from below
      // Goei: weaving left/right sinusoidal dive
      // Commander: single wide loop before returning

      public static Vec2[] MakeDivePath(Vec2 from, float playerX, EnemyType type) {
        Vec2 p0 = from;

        if (type == EnemyType.Zako) {
          // Zako: swoop toward player, curve past bottom, loop up and back to top
          float curveDir = from.X > C.VIEWPORT_W * 0.5f ? -1f : 1f;
          Vec2 p1 = new Vec2(from.X + curveDir * 50, from.Y + 100);
          Vec2 p2 = new Vec2(playerX + curveDir * 30, C.VIEWPORT_H * 0.6f);
          Vec2 p3 = new Vec2(playerX, C.VIEWPORT_H * 0.85f);
          // Loop under and back
          Vec2 p4 = new Vec2(playerX - curveDir * 40, C.VIEWPORT_H + 30);
          Vec2 p5 = new Vec2(from.X - curveDir * 100, C.VIEWPORT_H + 40);
          Vec2 p6 = new Vec2(from.X - curveDir * 80, C.VIEWPORT_H * 0.3f);
          // Return to formation area (off top to wrap around)
          Vec2 p7 = new Vec2(from.X - curveDir * 40, -30);
          Vec2 p8 = new Vec2(from.X, -20);
          Vec2 p9 = from;
          return new Vec2[] { p0, p1, p2, p3, p4, p5, p6, p7, p8, p9 };
        }

        if (type == EnemyType.Goei) {
          // Goei: weaving S-curve dive, steering left and right
          float weaveAmp = 80f;
          float dir = from.X > C.VIEWPORT_W * 0.5f ? -1f : 1f;
          Vec2 p1 = new Vec2(from.X + dir * weaveAmp, from.Y + 80);
          Vec2 p2 = new Vec2(from.X - dir * weaveAmp * 0.5f, from.Y + 200);
          Vec2 p3 = new Vec2(from.X + dir * weaveAmp, C.VIEWPORT_H * 0.55f);
          // Second weave toward player
          Vec2 p4 = new Vec2(playerX - dir * 40, C.VIEWPORT_H * 0.65f);
          Vec2 p5 = new Vec2(playerX + dir * 30, C.VIEWPORT_H * 0.8f);
          Vec2 p6 = new Vec2(playerX, C.VIEWPORT_H + 30);
          // Loop back
          Vec2 p7 = new Vec2(from.X, C.VIEWPORT_H + 20);
          Vec2 p8 = new Vec2(from.X, -20);
          Vec2 p9 = from;
          return new Vec2[] { p0, p1, p2, p3, p4, p5, p6, p7, p8, p9 };
        }

        // Commander: wide sweeping loop, one big arc
        float cDir = from.X > C.VIEWPORT_W * 0.5f ? -1f : 1f;
        Vec2 c1 = new Vec2(from.X + cDir * 100, from.Y + 60);
        Vec2 c2 = new Vec2(C.VIEWPORT_W * 0.5f + cDir * 120, C.VIEWPORT_H * 0.4f);
        Vec2 c3 = new Vec2(C.VIEWPORT_W * 0.5f, C.VIEWPORT_H * 0.55f);
        Vec2 c4 = new Vec2(C.VIEWPORT_W * 0.5f - cDir * 80, C.VIEWPORT_H * 0.5f);
        Vec2 c5 = new Vec2(from.X - cDir * 60, C.VIEWPORT_H * 0.3f);
        Vec2 c6 = from;
        return new Vec2[] { p0, c1, c2, c3, c4, c5, c6 };
      }

      // Tractor beam: commander dives straight down to mid-screen position
      public static Vec2[] MakeTractorDivePath(Vec2 from, float playerX) {
        float targetX = Mathf.Clamp(playerX, C.ENTITY_SIZE, C.VIEWPORT_W - C.ENTITY_SIZE * 2);
        // Beam sprite is 80px tall at scale 2 = 160px. Beam starts at commanderY + ENTITY_SIZE.
        // Player is at VIEWPORT_H - ENTITY_SIZE - 20 = 648.
        // Need commanderY + ENTITY_SIZE + 160 >= 648, so commanderY >= 456.
        float playerLineY = C.VIEWPORT_H - C.ENTITY_SIZE - 20;
        int beamH = 80 * C.SPRITE_SCALE; // 160
        float targetY = playerLineY - beamH - C.ENTITY_SIZE + 30; // 648 - 160 - 32 + 30 = 486
        Vec2 p0 = from;
        Vec2 p1 = new Vec2(from.X, from.Y + 40);
        Vec2 p2 = new Vec2(targetX, targetY - 60);
        Vec2 p3 = new Vec2(targetX, targetY);
        return new Vec2[] { p0, p1, p2, p3 };
      }

      // Return path: tractor beam commander flies back to formation
      public static Vec2[] MakeReturnPath(Vec2 from, Vec2 target) {
        Vec2 p0 = from;
        Vec2 p1 = new Vec2(from.X, from.Y - 50);
        Vec2 p2 = new Vec2(target.X, target.Y + 40);
        Vec2 p3 = target;
        return new Vec2[] { p0, p1, p2, p3 };
      }

      public static float AngleFromPath(Vec2[] path, float t) {
        float dt = 0.01f;
        Vec2 a = EvalPath(path, Mathf.Max(0, t - dt));
        Vec2 b = EvalPath(path, Mathf.Min(1, t + dt));
        Vec2 dir = b - a;
        return Mathf.Atan2(dir.X, -dir.Y);
      }
    }

    // ───── Collision ─────

    public static class Collision {
      public static bool Overlaps(float ax, float ay, float aw, float ah,
      float bx, float by, float bw, float bh) {
        return ax + aw > bx && ax < bx + bw && ay + ah > by && ay < by + bh;
      }

      public static bool BulletHitsEntity(BulletState b, float ex, float ey, int size) {
        float margin = size * 0.15f;
        return Overlaps(b.X - 4, b.Y - 4, 8, 8,
        ex + margin, ey + margin, size - margin * 2, size - margin * 2);
      }
    }
  }
}
