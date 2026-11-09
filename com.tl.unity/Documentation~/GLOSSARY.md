# Tl in plain words

**Timeline**: a named list of tracks and markers with a length. It also says what happens at its end: stop, loop, hold the last frame, or play the next timeline. In Unity it is a Timeline asset.

**Track**: one row in the Timeline window. It holds clips of one clip type and knows how to turn a clip into a value (see Feeder).

**Clip**: a piece of data on a track: a window of time (a start tick and an end tick) and the values you set in the Inspector, such as a strength or a direction.

**Kind**: the rule for one kind of change, for example "push" or "health". It has two small functions: Combine adds up what every active clip wants, and Apply writes the total into the game state.

**Combine**: the kind function that merges two values into one. Sum is the usual choice. A priority rule (the higher priority wins) is the other.

**Apply**: the kind function that writes the combined value into the entity's data (a position, a health number, a bag). It runs once per tick for each kind that has something to say.

**Feeder**: the track function that reads a clip and gives a kind its value for this tick. It is marked `[Feeds(...)]`.

**Playback**: one running copy of a timeline on one entity. It remembers which timeline, which tick it is on, which channel it uses, and whether it is paused, reversed, held or running at a different speed. It is eight bytes.

**Channel**: a lane for timelines on one entity. Only one timeline plays per channel: starting another one on the same channel replaces the first. "None" means the timeline is on no channel and several can play at once. Names come from Project Settings > Tl.

**Tick**: one step of Tl's clock. The default is 60 ticks per second (Project Settings > Tl). Clips are rounded to whole ticks when they bake.

**Pulse**: a one-time effect at an instant: when a clip starts, when it ends, at a marker, or sent by game code. A pulse is applied exactly once. Because it knows which way time is running, rewinding takes it back.

**Marker**: a point on a timeline (not a stretch of time) that fires a pulse, for example "pick up the coin at tick 420".

**Speed**: how many ticks a playback crosses per step, in sixteenths: 16 is normal, 32 is twice as fast, 8 is half as fast. Kinds and pulses stay exact at every speed.

**Rewind**: running the whole world backward one tick per step. Sums and pulses undo exactly. A value that is simply set, such as a position, comes back only if something sets it again.

**Baking**: turning authored data (Timeline assets in Unity, or JSON) into the compact bytes the game plays. In Unity it happens when the SubScene bakes.

**Blob**: a read-only block of memory made by baking. The baked timeline bytes sit in a blob on the entity.

**State hash**: one 64-bit number that summarizes every playback and every piece of Tl state after each step. Two hosts that print the same number after the same steps agree on everything. Tl uses it to prove the command line, Unity and the IL2CPP and CoreCLR players do the same thing.

**Burst**: Unity's compiler that makes code fast. Tl's per-tick work is Burst code.

**Job**: a small piece of work Unity can run on several threads. Tl's per-tick work and your own `IJobEntity` code are jobs.

**Scene (JSON)**: a small file that lists entities, their starting state, which timelines they play, and requests at given steps. `tl run` plays it without an engine, and Unity plays the same file and gets the same hash.
