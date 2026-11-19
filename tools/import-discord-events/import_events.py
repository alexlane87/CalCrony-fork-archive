#!/usr/bin/env python3
"""One-off importer: Discord scheduled events -> CalCrony events.  Python 3.9+, standard library only
(on Windows also `pip install tzdata`).

  export   Read a server's upcoming Discord scheduled events (GET requests only) into a JSON file.
           Needs the bot token: DISCORD_BOT_TOKEN in --env-file or the environment.
  lookup   Find member and role ids by name (GET only), for events that exist only as a third-party
           bot's posts (Apollo shows nicknames, not ids) and have to be written into the JSON by hand.
  apply    Create them in CalCrony through its API. A DRY RUN unless --execute is given.
           Needs CALCRONY_API_KEY (the bot's API key) in --env-file or the environment.

Why a script and not just POST /events: a bot-authenticated create does not post the Discord
embed (the bot does that itself after /create), and RSVPs set afterwards never re-render a posted
embed. So `apply` uses CreateEventRequest's bot-only PostViaOutbox / InitialAttendeeIds /
NativeEventId fields: the API queues the post, the attendees are saved before the first render,
and the existing Discord event is adopted as the native twin (no duplicate when mirroring is on).

Discord's "interested" is not an RSVP; each interested member becomes "going". Review the dry run.
Re-running is safe: events already linked to a native id, or with the same title and start, are skipped.
See README.md in this folder.
"""
import argparse, json, os, sys, time, urllib.error, urllib.parse, urllib.request
from datetime import datetime, timedelta, timezone

DISCORD = "https://discord.com/api/v10"
UA = "DiscordBot (https://github.com/alexlane87/CalCrony, 1.0) event-import"
ACTIVE_STATUSES = (1, 2)  # scheduled, active


def read_env(path):
    values = {}
    if path:
        for line in open(os.path.expanduser(path), encoding="utf-8"):
            line = line.strip()
            if line and not line.startswith("#") and "=" in line:
                k, v = line.split("=", 1)
                values[k.strip()] = v.strip().strip('"').strip("'")
    return values


def setting(env, key, default=None):
    return os.environ.get(key) or env.get(key) or default


def http(method, url, headers=None, body=None):
    data = None if body is None else json.dumps(body).encode()
    h = {"User-Agent": UA, "Content-Type": "application/json", **(headers or {})}
    for _ in range(6):
        req = urllib.request.Request(url, data=data, method=method, headers=h)
        try:
            with urllib.request.urlopen(req, timeout=30) as r:
                raw = r.read()
                return r.status, (json.loads(raw) if raw else None)
        except urllib.error.HTTPError as e:
            raw = e.read()
            try:
                payload = json.loads(raw) if raw else None
            except ValueError:
                payload = raw.decode(errors="replace")
            if e.code == 429 and isinstance(payload, dict):
                time.sleep(float(payload.get("retry_after", 1)) + 0.25)
                continue
            return e.code, payload
    return 429, None


def parse_iso(text):
    return datetime.fromisoformat(text.replace("Z", "+00:00")).astimezone(timezone.utc)


# ---------------------------------------------------------------- export
def cmd_export(a):
    env = read_env(a.env_file)
    token = setting(env, "DISCORD_BOT_TOKEN")
    if not token:
        sys.exit("DISCORD_BOT_TOKEN not found (use --env-file or the environment).")
    auth = {"Authorization": "Bot " + token}

    st, chans = http("GET", f"{DISCORD}/guilds/{a.guild}/channels", auth)
    chan_names = {c["id"]: c["name"] for c in (chans or [])} if st == 200 else {}
    st, evs = http("GET", f"{DISCORD}/guilds/{a.guild}/scheduled-events?with_user_count=true", auth)
    if st != 200:
        sys.exit(f"Could not list scheduled events: HTTP {st} {evs}")

    out = []
    for e in sorted(evs, key=lambda e: e["scheduled_start_time"]):
        if e["status"] not in ACTIVE_STATUSES:
            continue
        users, after = [], "0"
        while True:
            st, page = http("GET", f"{DISCORD}/guilds/{a.guild}/scheduled-events/{e['id']}/users?limit=100&after={after}", auth)
            if st != 200 or not page:
                break
            users += [u["user_id"] for u in page]
            after = page[-1]["user_id"]
            if len(page) < 100:
                break
        location = (e.get("entity_metadata") or {}).get("location")
        if not location and e.get("channel_id"):
            location = "#" + chan_names.get(e["channel_id"], "voice channel")
        out.append({
            "native_id": e["id"],
            "name": e["name"],
            "description": e.get("description") or "",
            "location": location or "",
            "start": e["scheduled_start_time"],
            "end": e.get("scheduled_end_time"),
            "creator_id": e.get("creator_id"),
            "image_url": (f"https://cdn.discordapp.com/guild-events/{e['id']}/{e['image']}.png?size=1024"
                          if e.get("image") else None),
            "interested": users,
        })
    with open(a.out, "w", encoding="utf-8") as f:
        json.dump({"guild": a.guild, "exported_at": datetime.now(timezone.utc).isoformat(), "events": out}, f, indent=2)
    try:
        os.chmod(a.out, 0o600)
    except OSError:
        pass
    print(f"Wrote {len(out)} upcoming events to {a.out} (contains member ids: keep it private).")
    for e in out:
        print(f"  {e['start'][:16]}  {len(e['interested'])} interested  {e['name']}")


# ---------------------------------------------------------------- lookup
def cmd_lookup(a):
    env = read_env(a.env_file)
    token = setting(env, "DISCORD_BOT_TOKEN")
    if not token:
        sys.exit("DISCORD_BOT_TOKEN not found (use --env-file or the environment).")
    auth = {"Authorization": "Bot " + token}
    for name in a.member or []:
        q = urllib.parse.quote(name)
        st, found = http("GET", f"{DISCORD}/guilds/{a.guild}/members/search?query={q}&limit=10", auth)
        print(f"member {name!r}: " + ("no match" if not found else f"{len(found)} match(es)") if st == 200 else f"member {name!r}: HTTP {st}")
        for m in (found or []) if st == 200 else []:
            u = m["user"]
            print(f"   {u['id']}  username={u['username']!r}  global={u.get('global_name')!r}  nick={m.get('nick')!r}")
    if a.role:
        st, roles = http("GET", f"{DISCORD}/guilds/{a.guild}/roles", auth)
        for name in a.role:
            hits = [r for r in (roles or []) if name.lower() in r["name"].lower()] if st == 200 else []
            print(f"role {name!r}: " + ("no match" if not hits else f"{len(hits)} match(es)"))
            for r in hits:
                print(f"   {r['id']}  {r['name']!r}  managed={r.get('managed')}")


# ---------------------------------------------------------------- apply
def local_texts(start_utc, zone):
    local = start_utc.astimezone(zone)
    # ISO-ish first, then a spelled-out form; each is verified against CalCrony's own parser.
    return [local.strftime("%Y-%m-%d %H:%M"),
            f"{local.day} {local.strftime('%B %Y')} at {local.strftime('%H:%M')}"]


def cmd_apply(a):
    try:
        from zoneinfo import ZoneInfo
    except ImportError:
        sys.exit("Python 3.9+ is needed.")
    env = read_env(a.env_file)
    key = setting(env, "CALCRONY_API_KEY")
    if not key:
        sys.exit("CALCRONY_API_KEY not found (use --env-file or the environment).")
    api = (a.api or "http://{}:{}".format(setting(env, "CALCRONY_BIND_IP", "127.0.0.1"),
                                          setting(env, "CALCRONY_API_PORT", "8080"))).rstrip("/")
    auth = {"X-Api-Key": key}
    src = json.load(open(a.file, encoding="utf-8"))
    guild = a.guild or src["guild"]
    repeats = {}
    for item in a.repeat or []:
        title, _, spec = item.rpartition("=")
        if not title or not spec[:-1].isdigit() or spec[-1] not in "wd":
            sys.exit(f'--repeat expects "Exact title=2w" (weeks) or "Exact title=3d" (days), got: {item}')
        repeats[title.strip()] = (1 if spec[-1] == "w" else 0, int(spec[:-1]))

    skip = {t.strip() for t in a.skip or []}

    st, settings = http("GET", f"{api}/guilds/{guild}/settings", auth)
    if st != 200 or not settings:
        sys.exit(f"Could not read server settings from {api} (HTTP {st}): {settings}")
    if not settings.get("defaultChannelId"):
        sys.exit("This server has no default events channel set; run /settings default-channel in Discord first.")
    try:
        zone = ZoneInfo(settings["timeZone"])
    except Exception as ex:  # tzdata missing on Windows
        sys.exit(f"Time zone {settings['timeZone']!r} unavailable ({ex}). On Windows: pip install tzdata")
    channel = settings["defaultChannelId"]

    st, existing = http("GET", f"{api}/guilds/{guild}/events?includePast=true", auth)
    if st != 200:
        sys.exit(f"Could not list existing events (HTTP {st}).")
    linked = {str(e["nativeEventId"]) for e in existing if e.get("nativeEventId")}
    seen = {(e["title"].strip().lower(), parse_iso(e["startsAtUtc"])) for e in existing}

    print(f"{'EXECUTING' if a.execute else 'DRY RUN'}: {api}  server {guild}  zone {zone.key}  channel {channel}\n")
    problems = created = 0
    for e in src["events"]:
        start = parse_iso(e["start"])
        end = parse_iso(e["end"]) if e.get("end") else None
        tag = f"{start.astimezone(zone):%a %d %b %Y %H:%M}  {e['name']}"
        if e["name"].strip() in skip:
            print(f"SKIP   {tag}\n       excluded with --skip"); continue
        if e.get("native_id") and e["native_id"] in linked:
            print(f"SKIP   {tag}\n       already linked to this Discord event"); continue
        if (e["name"].strip().lower(), start) in seen:
            print(f"SKIP   {tag}\n       an event with this title and start already exists"); continue
        if start <= datetime.now(timezone.utc):
            print(f"SKIP   {tag}\n       already started"); continue

        when_text = None
        for text in local_texts(start, zone):
            st, parsed = http("POST", f"{api}/tools/parse-datetime", auth, {"text": text, "guildId": int(guild)})
            if st == 200 and parse_iso(parsed["utc"]) == start:
                when_text = text; break
        if not when_text:
            problems += 1
            print(f"ERROR  {tag}\n       CalCrony's parser did not resolve the start to {start:%Y-%m-%d %H:%M} UTC"); continue

        body = {
            "creatorId": int(e["creator_id"]) if e.get("creator_id") else 0,
            "title": e["name"][:100],
            "whenText": when_text,
            "channelId": int(channel),
            "description": e["description"] or None,
            "durationMinutes": (max(1, round((end - start).total_seconds() / 60)) if end
                                else e.get("duration_minutes")),
            "location": e["location"] or None,
            "imageUrl": e.get("image_url"),
            "postViaOutbox": True,
            "initialAttendeeIds": [] if a.no_attendees else [int(u) for u in e.get("going") or e.get("interested") or []],
            "nativeEventId": int(e["native_id"]) if e.get("native_id") and not a.no_link else None,
        }
        if e.get("allowed_role_ids"):
            body["allowedRoleIds"] = [int(r) for r in e["allowed_role_ids"]]
        if e.get("attendee_limit"):
            body["attendeeLimit"] = int(e["attendee_limit"])
        rule = repeats.get(e["name"].strip())
        if rule:
            body["recurrence"] = {"unit": rule[0], "interval": rule[1], "monthlyMode": 0, "daysOfWeek": 0}
        what = "repeats every {} {}".format(rule[1], "week(s)" if rule[0] == 1 else "day(s)") if rule else "single event"
        print(f"{'CREATE' if a.execute else 'WOULD '} {tag}\n       {what}; {len(body['initialAttendeeIds'])} going; "
              f"{'adopts Discord event ' + str(body['nativeEventId']) if body['nativeEventId'] else 'no Discord link'}; "
              f"{'limit ' + str(body['attendeeLimit']) + '; ' if body.get('attendeeLimit') else ''}"
              f"{'signup restricted to ' + str(len(body['allowedRoleIds'])) + ' role(s); ' if body.get('allowedRoleIds') else ''}"
              f"duration {body['durationMinutes']} min; location {body['location']!r}; start text {when_text!r}")
        if not a.execute:
            continue
        st, made = http("POST", f"{api}/guilds/{guild}/events", auth, body)
        if st == 201:
            # An API older than this tool ignores the bot-only fields: the event would exist but never be
            # posted and carry no attendees. Check, and if so undo this one and stop.
            wrong = []
            if len(made.get("rsvps") or []) != len(set(body["initialAttendeeIds"])):
                wrong.append("attendees were not saved")
            if body["nativeEventId"] and made.get("nativeEventId") != body["nativeEventId"]:
                wrong.append("the Discord event was not adopted")
            if wrong:
                http("DELETE", f"{api}/events/{made['id']}", auth)
                sys.exit(f"STOPPED: {' and '.join(wrong)}. This CalCrony API is probably older than the importer "
                         f"(deploy the version with PostViaOutbox first). The event just created was deleted again; "
                         f"{created} earlier event(s) from this run are fine.")
            created += 1
            print(f"       created {made['id']}")
            seen.add((e["name"].strip().lower(), start))
        else:
            problems += 1
            print(f"ERROR  HTTP {st}: {made}")
    print(f"\n{'Created ' + str(created) + '. ' if a.execute else ''}{problems} problem(s)."
          + ("" if a.execute else "  Nothing was changed. Re-run with --execute to create these."))
    sys.exit(1 if problems else 0)


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    x = sub.add_parser("export", help="read upcoming Discord scheduled events into a JSON file")
    x.add_argument("--guild", required=True)
    x.add_argument("--out", required=True)
    x.add_argument("--env-file")
    x.set_defaults(fn=cmd_export)
    z = sub.add_parser("lookup", help="find member / role ids by name (read only)")
    z.add_argument("--guild", required=True)
    z.add_argument("--env-file")
    z.add_argument("--member", action="append", help="name to search (nickname or username prefix); repeatable")
    z.add_argument("--role", action="append", help="role name fragment; repeatable")
    z.set_defaults(fn=cmd_lookup)
    y = sub.add_parser("apply", help="create them in CalCrony (dry run unless --execute)")
    y.add_argument("--file", required=True)
    y.add_argument("--guild", help="target server id (default: the one in the file)")
    y.add_argument("--api", help="CalCrony API base URL (default from CALCRONY_BIND_IP/CALCRONY_API_PORT in --env-file)")
    y.add_argument("--env-file")
    y.add_argument("--repeat", action="append", metavar='"TITLE=2w"', help="make this event a repeating series (weeks w / days d)")
    y.add_argument("--skip", action="append", metavar='"TITLE"', help="leave out the event with this exact title; repeatable")
    y.add_argument("--no-attendees", action="store_true", help="do not carry the interested members over")
    y.add_argument("--no-link", action="store_true", help="do not adopt the Discord event (use when importing into a different server)")
    y.add_argument("--execute", action="store_true", help="actually create the events")
    y.set_defaults(fn=cmd_apply)
    a = p.parse_args()
    a.fn(a)


if __name__ == "__main__":
    main()
