# Import existing Discord events into CalCrony

A one-off tool for bringing a server's existing events over when it adopts CalCrony. Written for the
West Country Leaders move off Apollo (2026-11), where the real events were all Discord scheduled
events created by hand.

**What it moves:** upcoming Discord scheduled events: title, description, location, start, length,
cover image, creator, and the members who clicked "Interested". Those become "going" RSVPs.
**What it does not:** past events, and anything only a third-party bot holds. Apollo keeps its RSVP
lists in the embeds of its own messages, which a bot can only read with the Message Content privileged
intent, and Apollo has no export. If Apollo posts still exist and matter, read them by hand.

## How it works

`export` reads the server's scheduled events from Discord (GET only) into a JSON file.
`apply` creates them through CalCrony's API. It needs the bot's API key, and it uses three optional,
bot-only fields on `CreateEventRequest`:

| Field | Why a script needs it |
|---|---|
| `PostViaOutbox` | A bot-authenticated create does not post the Discord embed (the bot does that after `/create`). This asks the API to queue the post. |
| `InitialAttendeeIds` | Seated before the first render. RSVPs set later by a bot never re-render a posted embed. |
| `NativeEventId` | Adopts the existing Discord event as the native twin, so mirroring updates it rather than creating a second one. |

Each start is checked through CalCrony's own date parser before anything is created, so a time-zone
slip fails loudly. `apply` is a **dry run** unless you pass `--execute`, and is safe to repeat: events
already linked to the Discord event, or with the same title and start, are skipped.

## Running it (prod, on the Pi)

```bash
# 1. read Discord (uses the prod bot token from the stack's .env; it never leaves the Pi)
python3 import_events.py export --guild <server id> --out ~/event-scan/events.json --env-file ~/calcrony/.env
# 2. look at what would happen
python3 import_events.py apply --file ~/event-scan/events.json --env-file ~/calcrony/.env \
    --repeat "Regular Root/Arcs at Replay=2w"
# 3. do it
python3 import_events.py apply ... --execute
```

The API URL defaults to `CALCRONY_BIND_IP:CALCRONY_API_PORT` from the env file. Repeating events are named
with `--repeat "Exact title=2w"` (weeks) or `=3d` (days); the rule is anchored on the event's own weekday.

## Rehearsing on the local dev stack

Create a scheduled event in your dev server, then run `export` with `--env-file .env.local` and `apply`
with `--api http://localhost:8080 --env-file .env.local`. Importing into a *different* server than the one
exported (for example prod data into dev) needs `--guild <target>` and `--no-link`, because the Discord event
belongs to the source server. Add `--no-attendees` too, as the members are not in the target.

## Notes

- The exported JSON holds member ids. Keep it private and delete it afterwards.
- Turn on `MirrorNativeEvents` (`/settings`) afterwards if you want later occurrences of a repeating event to
  get Discord scheduled events of their own. The adopted event keeps being updated either way.
- On Windows, `pip install tzdata` first (Python there has no time-zone database).
