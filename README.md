# Agent Risk

A strategy board game in the classic world-conquest format, built so
that AI agents are first-class players. Agents and humans sit at the
same table, read the same board, and answer the same prompts. Every
game produces a complete, replayable record.

The game follows the standard rules on the standard world board:
42 territories, six continents, territory cards, continent bonuses,
and world domination as the only victory condition. Games seat 2 to 6
players. A two-player game runs in Duo mode, where a neutral Rebel
army holds a third of the board and eliminating the other player ends
the game. The host never plays a move for a silent seat: a player who
cannot answer pauses the game for the host instead.

## What is in this repository

- `client/`: the desktop app (Godot 4, C#). It hosts a table on the
  local network, joins one, or watches one, with the full board,
  per-seat transcripts, and host controls including a guarded
  position editor.
- `src/board/`, `src/engine/`, and `src/host/`: the libraries the app
  builds with: the board data, the rules engine, and the game host.

## How a game works

One player hosts the table, and the host's copy of the app runs the
game. The host chooses the seats before the game starts. Each seat is
one of four kinds:

- **This screen**: a person playing in the host's own window.
- **Client**: a person playing in the app on another computer,
  joined over the local network.
- **Agent**: an AI player, connected over the network.
- **Stand-in**: a built-in scripted player that fills a seat, for a
  practice game or a smaller group.

Play runs one decision at a time. The game sends the player whose
turn it is a prompt with the current board, the cards in hand, and the
legal moves. A person answers on screen. An agent receives the same
prompt as text and replies with one line. Every roll and result goes
to everyone at the table. The game writes a complete record (every
prompt, reply, and roll) that can be replayed and checked afterward,
and a suspended game can be resumed exactly where it stopped.

## Building the Windows app

You need the .NET 8 SDK and Godot 4.7.2, **mono (.NET) edition**. This
is a C# project, so both the editor and the export templates must be
the mono packages:

- Editor: `Godot_v4.7.2-stable_mono_win64`.
- Templates: `Godot_v4.7.2-stable_mono_export_templates.tpz`, which
  installs to `%APPDATA%\Godot\export_templates\4.7.2.stable.mono\`,
  beside the standard set, not over it.

The standard (non-mono) editor and templates cannot export this
project. Their export still exits 0, but the result has no game
assembly.

From `client/`, run:

```powershell
.\build-windows.ps1
```

The script finds the mono editor (through `-GodotPath`, then
`$env:GODOT`, then `PATH`) and checks for the templates. It runs the
"Windows Desktop" export preset, then confirms the game assembly is
present and that the exported executable boots. It exits nonzero with
a plain reason if any step fails.

The output is in `client/build/windows/`. Ship `AgentRisk.exe`,
`AgentRisk.pck`, and the `data_AgentRiskClient_windows_x86_64` folder
together; the executable does not run without the other two.

To run the game without exporting it, open `client/` in the mono
editor and press Play.

## Connecting an agent

An agent needs no SDK. Any program that can open a TCP connection and
read and write lines of UTF-8 text can play.

1. The host sets a seat to **Agent** on the *Host a game* tab. The app
   shows a ready-to-paste instruction block for that seat with the
   address, port (default `4000`), and seat code filled in.
2. The agent connects. The host greets it with a line listing the open
   seats, and the agent answers with one line, for example `JOIN RED`.
   Seat codes are `RED`, `BLU`, `GRN`, `YEL`, `PNK`, and `BLK`.
3. The agent receives a one-time briefing: its seat, the game mode,
   starting armies, and the full board (continents, bonuses,
   territories, and borders), plus the card rules.
4. Each decision then arrives as one prompt with a summary of
   everything since the agent's last decision, the full board, the
   agent's own cards, and the decision being asked with the exact
   reply forms it accepts. The agent answers with exactly one line.

Every block the host sends ends with a marker line: `<<REPLY>>` when
it expects one reply line, `<<END>>` when it expects none. Lines end
in `\n`.

If a reply is not legal, the agent is prompted again with the reason.
If that reply also fails, it gets a numbered list of legal moves to
choose from. If it still cannot answer, or the connection drops, the
game pauses for the host, who can poke the seat, replace it, suspend
the game, or end it.

To watch a game instead, send `JOIN WATCH` after the greeting and read
the live transcript feed. A watcher sees every seat's cards, so share
the host address only with people you trust with that.

`client/data/` holds sample briefing and prompt texts that show the
format.

To test a hosted table before writing an agent, start a second copy
of the app in autopilot mode. It joins the seat and answers every
decision with the first legal move:

```powershell
.\AgentRisk.exe -- --autopilot HOST PORT SEAT
```

## License

The source code is released under the [MIT License](LICENSE).

Agent Risk is an unofficial fan project. It is not affiliated with,
endorsed by, or connected to Hasbro. Risk is a trademark of Hasbro.
