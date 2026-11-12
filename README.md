# GorillaFlags

A BepInEx mod for Gorilla Tag that puts any flag on your flag. Pick from 310 flags or your own images,
carry it on your back, hold it, plant it in the ground, throw it and climb it.

- [Features](#features)
- [Install](#install)
- [User guide](#user-guide)
  - [1. Opening the menu](#1-opening-the-menu)
  - [2. Choosing a flag](#2-choosing-a-flag)
  - [3. Private flag or Variant](#3-private-flag-or-variant)
  - [4. Holding and planting](#4-holding-and-planting)
  - [5. Throwing and catching](#5-throwing-and-catching)
  - [6. GF Sandbox (the button on the pole)](#6-gf-sandbox-the-button-on-the-pole)
  - [7. Playing with friends (private rooms)](#7-playing-with-friends-private-rooms)
  - [8. Climbing flags](#8-climbing-flags)
  - [9. Gorebox weapons](#9-gorebox-weapons)
  - [10. Your own images](#10-your-own-images)
  - [11. All settings](#11-all-settings)
- [Controls](#controls)
- [Troubleshooting](#troubleshooting)
- [Settings file](#settings-file)
- [Building from source](#building-from-source)

## Features

- **310 flags built in:** 200 countries, 75 territories and regions, 12 organizations (UN, EU, NATO,
  Organization of Turkic States, African Union, Arab League, ASEAN, CIS, Council of Europe, CSTO, OIC, SCO)
  and 23 Pride flags. Search, favorites and your own images.
- **Private flag:** Your own flag on your back. You don't need to own the flag cosmetic.
- **Plant it** in the ground, on walls and on cars. **Throw it**, watch it tumble and bounce, and catch it in the air.
- **Real cloth:** The flag waves with the game's own spring physics. Wind on planted flags.
- **GF Sandbox:** Pole length, pole thickness, flag size, upright, balance base, waving, wind and lock.
- **Climbable** planted flags in modded (Utilla) lobbies.
- **Private rooms:** Friends with the mod see your flag and can carry, drop and replant it.
- **Gorebox weapons (optional):** Planted flags have 100 HP, get bullet holes and break apart.
- **Menus** on two wall screens in City and on the desktop. English, Turkish, Spanish and Russian.

## Install

1. Install **BepInEx 5** for Gorilla Tag.
2. Download the latest zip from [Releases](../../releases).
3. Put `GorillaFlags.dll` into `Gorilla Tag\BepInEx\plugins`.
4. Start the game. The flag appears on your back (see [Private flag or Variant](#3-private-flag-or-variant)).

Optional: [Utilla](https://github.com/legoandmars/Utilla) for climbable flags in modded lobbies, and the
Gorebox mod for weapon damage.

## User guide

### 1. Opening the menu

There are two ways to open the GorillaFlags menu:

- **In VR:** Walk to one of the two wall screens in **City**: one in the tunnel between Forest and City,
  one on the wall next to the City wardrobe hut. Press the buttons with your **index or middle finger**,
  like any Gorilla Tag button.
- **On the desktop:** Press **F** (you can change the key in **Settings > Keybinds**).

The menu has four tabs: **General**, **Flags**, **Pole** and **Settings**.

### 2. Choosing a flag

1. Open the **Flags** tab.
2. Pick a category at the top: **Custom**, **Countries**, **Territories**, **Organizations** or **Pride**.
3. Tap a flag. It is applied right away.
4. **Search:** type a flag's name in the search box (desktop menu). The English name and the name in
   your menu language both work.
5. **Favorites:** tap the **star** on a flag. Favorites stay at the top of their category.
6. **Original flag** goes back to the game's own flag.

**Flag sides** (Settings): each side of the cloth can be mirrored or turned upside down, for example
to fix a flag that reads backwards from one side.

### 3. Private flag or Variant

Open the **General** tab. There are two ways to show your flag:

| Option | Who sees it | Do I need the cosmetic? |
|---|---|---|
| **Private** | Only you. In private rooms, friends with the mod see it too. | No |
| **Variant** (Pride Flag / Trans Pride Flag) | Everyone in the lobby | Yes, you must own and wear that flag cosmetic |

- With **Private**, the mod gives you your own flag on your back. Choose the shoulder under **Side**
  (left or right). Planting, throwing, GF Sandbox and sharing all work with the Private flag.
- With **Variant**, the mod paints your chosen flag onto the real cosmetic. Change the shoulder from the
  wardrobe in game. The real cosmetic files are not changed.
- **Rotation** turns the cloth around its pole. **Quick angles** has the common angles as buttons.

### 4. Holding and planting

1. Reach to the pole on your back and hold **grip**. The flag comes into your hand.
2. Let go of grip slowly to put it back on your back.
3. To **plant** it: hold the **plant button** (**Y or B** by default) and hit the ground or a wall with the
   bottom of the pole. The pole stays at the angle you hit with (or straight up with **Upright**).
4. Grab a planted flag with **grip** to pick it up again.

Good to know:

- **Plant strength** (Settings) sets how hard you must hit: Softest, Soft, Normal, Hard or Hardest.
- Flags planted on cars and other moving things move with them.
- If you walk far away from your planted flag, it comes back to your back.
  The distance is set in **Settings > Return to back after** (30 m, 60 m, 120 m, 250 m or never).
  It also comes back when you leave the room.
- Turn planting off or change the buttons in **Settings > Plant flag** and **Keybinds**.

### 5. Throwing and catching

1. Hold the flag, swing your arm and let go of **grip** while swinging.
2. The flag flies, spins and flaps in the air, and can bounce off the ground.
3. It **plants itself** only when the tip of the pole hits the ground **hard**. A softer landing makes it
   tip over and lie on the ground.
4. **Catch** it by grabbing it in the air with **grip**.
5. A flag that lies on the ground can be picked up with grip.

Turn throwing off with **Settings > Throw & catch flag**.

### 6. GF Sandbox (the button on the pole)

When your flag is planted, a small button appears near the top of the pole. Tap it to open GF Sandbox.
It has two pages:

**Page 1**

- **Pole length:** make the pole taller or shorter.
- **Pole thickness:** thicker poles for tall flags.
- **Flag size:** make the cloth bigger, up to the pole length.
- **Upright:** the pole always stands straight up when planted.

**Page 2**

- **Balance base:** a round stand under the pole.
- **Wave:** how much the cloth waves.
- **Wind:** a steady wind that keeps the flag open (Off, Gentle, Medium, Strong).
- **Lock:** nobody can pick up the planted flag, not even you. Unlock it here.

Size changes go back to normal when the flag returns to your back. Hide the pole button with
**Settings > GF Sandbox**.

### 7. Playing with friends (private rooms)

Everything below works only in **private rooms** (not in public lobbies), and only with players who
also have GorillaFlags:

- They see your flag, where you planted it, how it waves, its size and its wind.
- **Others can take my flag** (Settings > Sharing): friends can pick up your planted flag, carry it,
  drop it, throw it and plant it somewhere else. Turn it off, or use **Lock** in GF Sandbox.
- **Mod users** (Settings) shows who in the room has the mod.

Everyone should use the same version of GorillaFlags.

### 8. Climbing flags

In modded lobbies (with Utilla), planted flags are solid: you can climb the pole like a tree.
Flags planted on cars do not block the car. Turn it off with **Settings > Climbable flag**.

### 9. Gorebox weapons

If the **Gorebox** mod is installed, its weapons can damage planted flags:

- A planted flag (or one lying on the ground) has **100 HP**. One shot does not destroy it.
- Every bullet leaves a burnt hole where it hits. The holes move with the cloth.
- Below 50 HP the pole starts to lean in the direction of the shots.
- Rockets and bombs hit hard: a direct hit destroys the flag in one go.
- At **0 HP** the flag breaks apart: pieces of cloth and pole fly off, and the flag falls over.
- A destroyed flag goes back to your back after **12 seconds**.
- Picking the flag up or having it on your back repairs it to 100 HP.

The flag's owner keeps track of its HP. In private rooms, friends with this version see the holes
and the break.

### 10. Your own images

1. Open the **Flags** tab, then **Custom**.
2. Tap **+ Add image** (desktop menu) and pick an image in the window that opens.
3. The image shows up in **Custom** and works like any other flag.

You can also copy PNG files into `Gorilla Tag\BepInEx\plugins\Flags` yourself and press **Reload flags**.

Custom images are shown to friends in private rooms only if they have the same image.

**Pole tab:** change the color of the pole, or use your own image for the pole (**Custom PNG**).
**Original pole** goes back to the default wood pole.

### 11. All settings

| Setting | What it does |
|---|---|
| **Plant flag** | Turns planting on or off |
| **Plant strength** | How hard you must hit to plant |
| **Keybinds** | The menu key and the two plant buttons |
| **Throw & catch flag** | Throwing on or off |
| **Return to back after** | Distance after which a planted flag comes back |
| **Climbable flag** | Solid, climbable flags in modded lobbies |
| **Sharing** | Others can take my flag |
| **GF Sandbox** | Show the button on the pole |
| **Flag sides** | Mirror or flip each side of the cloth |
| **Apply method** | Keep it on Array. Only change it if the flag looks broken |
| **Texture quality** | Normal, High or Ultra flag image |
| **Flag detail** | More cloth parts = smoother waves (only you see the difference) |
| **Appearance / Color / Font** | How the wall screen looks |
| **Language** | English, Türkçe, Español, Русский |
| **Reset all settings** | Everything back to default (tap twice) |

## Controls

| Input | Action |
|---|---|
| **Grip** on the pole | Take the flag from your back, or pick up a planted / lying flag |
| Let go of **grip** slowly | Put the flag back on your back |
| Hold **Y / B** and hit the ground or a wall with the pole | Plant the flag |
| Swing and let go of **grip** | Throw the flag |
| **Grip** a flying flag | Catch it |
| Index or middle finger on a wall screen | Press menu buttons |
| Small button on a planted pole | Open GF Sandbox |
| **F** (desktop) | Open / close the menu |

## Troubleshooting

- **"Flag not found. Equip it first."** You chose a Variant but you are not wearing that flag cosmetic.
  Wear it in the wardrobe, or use **Private** instead.
- **"Private and Variant are off."** Turn one of them on in the **General** tab.
- **My friend can't see my flag.** Sharing only works in private rooms, and your friend needs GorillaFlags
  (the same version).
- **My friend sees a white flag.** You are using a Custom image your friend does not have.
- **The flag looks broken or is the wrong flag.** Try another **Apply method** in Settings.
- **Organizations folder:** older versions loaded organization flags from `plugins\Flags\Organizations`.
  They are now built in, so you can delete that folder.

## Settings file

After the first launch, `Gorilla Tag\BepInEx\config\gorillaflag.cfg` is created.
Each setting is described inside the file.

## Building from source

Open `Flags.sln` in Visual Studio 2022 or newer and build, or use MSBuild:

```
msbuild Flags\Flags.csproj -p:Configuration=Release
```

`Flags\Flags.csproj` reads the game's DLLs from `D:\SteamLibrary\steamapps\common\Gorilla Tag`.
If your game is somewhere else, change the `HintPath` entries in the csproj.

The flag images in `Flags\bayraklar` and the atlas in `Flags\sablon` are embedded into the DLL.
