# Running nb in a container

The standard deployment is one container, nb inside it, the model's working tree
inside it too. nb does not confine the tools it runs, so the container is where the
confinement is, and nb shares everything in it with the model: the filesystem, the
network, the environment. This page is the invocation, written once, from the first
harness that ran it that way (proctor's `code-change` eval, 2026-09-22, nine cells in
nine containers on the .NET SDK image). It is not a security document: every mechanism
here keeps things out of the model's *accidental* reach and makes deliberate reads
visible in the transcript. None of them is a boundary. The container is.

## The image

nb ships as a layer. `Containerfile` at the repository root publishes the
self-contained linux-x64 binary into `/opt/nb` and nothing else goes with it: the
binary, `providers/`, `appsettings.example.json`. The developer's `appsettings.json`
and `mcp.json` never enter the build context (`.dockerignore` is an allowlist).

```bash
podman build -t nb .                        # or: docker build -f Containerfile -t nb .
```

Copy the layer onto whatever base the fixture needs and set the one environment
variable that does not travel with a `COPY`:

```Dockerfile
ARG NB_IMAGE=nb:latest
FROM ${NB_IMAGE} AS nb
FROM mcr.microsoft.com/dotnet/sdk:10.0
COPY --from=nb /opt/nb /opt/nb
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 NO_COLOR=1
ARG UID=1000
ARG GID=1000
RUN groupadd -o -g ${GID} agent && useradd -o -m -u ${UID} -g ${GID} agent
USER agent
```

`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` is what lets a self-contained .NET binary
start on a base with no libicu; without it nb aborts before it prints anything. The
user is explained under *Who owns the files*. The `-o` on both is because the SDK image
already has a uid 1000 and a fresh build should not have to know that.

What is in the image is what the model can read: nb, its providers, the base image's
toolchain, and the fixture once it is mounted. Nothing else should be there. In
particular the image holds no config and no key.

## The invocation

The program goes in on stdin, the transcript comes out on stdout, the chrome (tool
logs, warnings) comes out on stderr, and nb's exit code is the container's:

```bash
podman run -i --rm \
    -v "$PWD/fixture:/work" -w /work \
    -v "$PWD/nb.json:/nb/config.json:ro" -e OPENAI_API_KEY \
    --userns=keep-id \
    my-image /opt/nb/nb --output jsonl --config /nb/config.json - < program.jsonl > transcript.jsonl
```

Line by line:

- `-i` keeps stdin open for the program; there is no `-t`, since nothing here is a
  terminal and a pty would rewrite the bytes on both ends.
- `-w /work` is the working directory nb starts in, which is where the bash tool runs
  and the file tools resolve relative paths. It should be the path the program names,
  because the model will be told where it is and will `cd` there if the two differ.
- `--config` on a read-only mount, at a path the image does not otherwise use. The
  config names keys as `${VAR}` references and the values come in with `-e VAR`, one
  per key, never `--env-file` or the whole environment: nb interpolates them at load,
  and the model can read `/nb/config.json` and `env` alike, so the key is in its reach
  either way. What `-e` buys is that *only* that key is.
- `--userns=keep-id` is rootless podman; see *Who owns the files*. Docker's daemon is
  root and the flag does not exist there; the image's `USER` does the work instead.
- `/opt/nb/nb --output jsonl … -` is the same argv as on the host. `-` is stdin.

The exit codes are the CLI reference's (§2): 0 for a final answer, 1 for a startup or
config error emitted before any transcript, 2 provider error, 3 a budget or limit, 4
approval denied. A wrapper that captures stdout to a file and exits with the container's
code has the whole interface, which is how proctor's runner contract is written.

Two things a harness will meet on the first run:

- **docker buffers `exec` stdout.** Through `docker exec -i` the transcript arrives
  whole when nb exits rather than line by line. `podman run` and `docker run` stream.
  A harness that watches the transcript for progress should not rely on `exec`.
- **Name resolution.** A model server on the LAN that the host knows through
  `/etc/hosts` is unknown inside the container. `--add-host name:ip` (or a network the
  engine resolves) is the fix; nb's connection error names the host it could not
  resolve, which is the symptom.

## Who owns the files

The model writes into the mounted tree, and what it writes should belong to the caller
on the host, so the harness can read, diff and delete it without `sudo`.

- **Rootless podman** maps the caller's uid to root inside by default, so a file the
  model writes as root comes out on the host as the caller: convenient, until a tool
  in the image refuses to run as root. `--userns=keep-id` maps the caller to the same
  uid inside instead, and then the image needs a user with that uid or `HOME` and the
  toolchain's caches end up unwritable. The `ARG UID`/`ARG GID` above take the caller's
  ids at build time (`--build-arg UID=$(id -u)`) for that.
- **Docker** runs the container as the image's `USER` with no mapping at all, so the
  same `ARG UID`/`ARG GID` is the whole mechanism: build the image with the caller's
  ids and the files come out theirs. Build it with someone else's and every file the
  model writes is owned by a stranger.

Either way the working tree is bind-mounted, not copied: the harness materialises the
fixture on the host, mounts it, and reads the diff on the host afterwards. Nothing has
to be copied out of the container.

## The network

nb and the model share the network namespace. Whatever nb can reach to talk to its
provider, the model's bash and `fetch_url` can reach too. So the choice is:

- `--network none` when the provider is reachable another way (a socket mounted in, or
  a model that is itself in the container), which leaves the model nothing; or
- a network as narrow as nb's own need: the provider's host and nothing else, and
  `approval fetch` / `approval search` denied in the program so the tools that would
  use it are refused and the refusal is in the transcript.

The second is the common case and the live run used it: the provider on the LAN by
`--add-host`, the container's default network otherwise, `approval default deny` in
the program. That is an observation, not a control (`plans/approval-is-not-a-boundary.md`);
the control is the network.

## What nb leaves in reach

The container holds the fixture and nb, and nb's own artefacts are readable by the
model like anything else. The inventory, and the cheapest way to keep each out of
the tree:

| artefact | what it leaks | keep it out with |
| --- | --- | --- |
| the program file | the task, the system prompt, the path of every include | `nb --compile program.nb` on the host, the JSONL on stdin; nothing rests on disk |
| `@file` includes: an oracle sheet, a costume's instruction file | the answers | the same: `--compile` inlines every include, so the container never holds a path to one |
| `--seed` transcript | the fabricated history, as a file | the same again: `--compile --seed` folds it into the output |
| `appsettings.json` | the keys | keep keys as `${VAR}` in the config and pass them with `-e`; the key is still readable in the environment, but a config file with no literal key leaks nothing on its own |
| `mcp.json` | server commands and header values | there is none in the image; a `--mcp <file>` on a read-only mount when a run needs one, with headers as `${VAR}` too |
| `/opt/nb` itself: the binary, `providers/` | nothing that matters; the model can run nb, which is the same as running itself again | nothing; it is inert |
| the transcript | nothing inside: it is stdout, and stdout leaves the container | nothing |

`--compile` is the one that earns its paragraph. Parse, resolve every include, fold in
the seed, print the program as JSONL, run nothing:

```bash
nb --compile flow.nb > flow.jsonl          # on the host, beside the eval, where the includes are
podman run -i … /opt/nb/nb --output jsonl --config /nb/config.json - < flow.jsonl
```

It needs no provider, harness or config on the host, and a parse error or a directive nb
cannot honour fails there, exit 1, before a container exists for it. The compiled form
runs identically to the source (`evals/run.sh`, *compile*), and nb sniffs JSONL on
stdin, so the runner is the same command either way. The transcript already carries
the resolved sheet body rather than the path, so the compiled form is what the run
would have recorded anyway.

## Sharp edges

- **nb is not a boundary.** Every row above is about accidental contamination. A
  model that wants the key can `env`; a model that wants the sheet can read it out of
  its own context. What the mechanisms buy is that a read is a *tool call in the
  transcript*, which a harness can assert on, rather than something that happened
  in the model's working set without a trace.
- **The environment is shared.** `-e KEY` puts the key where the model can read it.
  There is no mode in which nb has the key and the model does not; they are one
  process tree in one namespace. Scope the key (a gateway route, a daily cap) rather
  than hide it.
- **`approval` observes.** `approval default deny` in the program refuses bash the
  policy does not name and records the refusal; it does not stop the file tools, and
  it does not stop the model from doing in `edit_file` what it was refused in bash.
- **Chrome is stderr; do not merge it.** A wrapper that does `2>&1` puts warnings into
  the transcript and breaks every consumer that parses it.
- **The exec form buffers.** If the harness keeps one container per cell and execs nb
  into it (proctor's shape, so setup and teardown hooks can own the container), the
  transcript through `docker exec` lands whole at the end. Fine for a batch; not a
  progress bar.
