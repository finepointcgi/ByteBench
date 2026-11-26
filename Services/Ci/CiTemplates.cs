namespace ByteBench.Services.Ci;

/// <summary>
/// Built-in jobs a <c>.bytebench-ci.yml</c> can pull in with <c>include: [name]</c> (or <c>include: name</c>). Each template is
/// ordinary job YAML, parsed and validated like the file's own jobs. A job with the same name in the file replaces the
/// template's, so a team can start from the template and override it by copying <see cref="Snippet"/>.
/// </summary>
public static class CiTemplates
{
    /// <summary>
    /// Night Owl (https://github.com/finepointcgi/Night-Owl), modelled on its GitLab template
    /// (<c>ci/gitlab/nightowl.gitlab-ci.yml</c>): installs the release with its checksum-verifying install script (skipped when
    /// <c>nightowl</c> is already on the image's PATH), runs <c>nightowl scan</c> and publishes the native JSON report and SARIF
    /// as <c>artifacts.reports</c>, which ByteBench reads into the repository's Security page, the pull request's Security tab
    /// and the Security merge check. Steps run in separate containers, so installing and scanning happen in one step.
    /// CI variables tune it: <c>NIGHTOWL_FAIL_ON</c> (critical|high|medium|low|none; empty uses <c>.nightowl/policy.yml</c>),
    /// <c>NIGHTOWL_PATH</c>, <c>NIGHTOWL_ARGS</c> (extra scan options, e.g. <c>--engines opengrep,gitleaks</c>),
    /// <c>NIGHTOWL_VERSION</c> and <c>NIGHTOWL_REPO</c>.
    /// Night Owl runs its engines from local binaries or pinned container images. ByteBench jobs never get the Docker socket, so
    /// the stock <c>alpine</c> image has neither: the job checks for that first and fails with instructions instead of a stack
    /// trace deep inside the scan. The supported setup is a runner image that has <c>nightowl</c> and the engines on PATH
    /// (see the README: "Night Owl runner image"): copy <see cref="Snippet"/> into the file and change <c>image:</c>.
    /// </summary>
    public const string NightOwl = """
        nightowl:
          image: alpine:3.20
          artifacts:
            when: always
            expire_in: 30d
            paths: [nightowl.md]
            reports:
              nightowl: nightowl.json
              sarif: nightowl.sarif
          steps:
            - name: Night Owl scan
              run: |
                set -e
                export NIGHTOWL_HOME="$PWD/.nightowl-home" NIGHTOWL_INSTALL_DIR="$PWD/.nightowl-bin"
                export PATH="$NIGHTOWL_INSTALL_DIR:$PATH" NIGHTOWL_AUTO_UPDATE=0
                if ! command -v nightowl >/dev/null 2>&1; then
                  if command -v apk >/dev/null 2>&1; then apk add --no-cache curl git bash gcompat libstdc++ icu-libs >/dev/null; fi
                  curl -fsSL "https://raw.githubusercontent.com/${NIGHTOWL_REPO:-finepointcgi/Night-Owl}/main/scripts/install.sh" | sh
                fi
                if ! command -v docker >/dev/null 2>&1 && ! command -v opengrep >/dev/null 2>&1 && ! command -v gitleaks >/dev/null 2>&1; then
                  echo "[nightowl] This image has no scan engines: there is no docker CLI and no opengrep or gitleaks on PATH." >&2
                  echo "[nightowl] ByteBench jobs do not get a Docker daemon. Use a runner image that has nightowl and its engines installed" >&2
                  echo "[nightowl] and set it as this job's image (copy the job from Settings > Security, see the README: Night Owl runner image)." >&2
                  exit 2
                fi
                failflag=""
                if [ -n "${NIGHTOWL_FAIL_ON:-}" ]; then failflag="--fail-on $NIGHTOWL_FAIL_ON"; fi
                nightowl scan "${NIGHTOWL_PATH:-.}" $failflag --no-update --format json --output nightowl.json \
                  --sarif-output nightowl.sarif --summary-output nightowl.md ${NIGHTOWL_ARGS:-}
        """;

    private static readonly Dictionary<string, string> All = new(StringComparer.OrdinalIgnoreCase) { ["nightowl"] = NightOwl };

    public static IReadOnlyCollection<string> Names => All.Keys;

    /// <summary>The YAML to paste into <c>.bytebench-ci.yml</c> under <c>jobs:</c> to customise a template.</summary>
    public static string? Snippet(string name) => All.TryGetValue(name, out var y) ? y : null;
}
