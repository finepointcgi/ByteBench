using YamlDotNet.Core;

namespace GitBench.Services.Ci;

/// <summary>
/// Built-in jobs a <c>.gitbench-ci.yml</c> can pull in with <c>include: [name]</c> (or <c>include: name</c>). Each template is
/// ordinary job YAML, parsed and validated like the file's own jobs. A job with the same name in the file replaces the
/// template's, so a team can start from the template and override it by copying <see cref="Snippet"/>.
/// </summary>
public static class CiTemplates
{
    /// <summary>
    /// Night Owl (https://github.com/finepointcgi/Night-Owl), modelled on its GitLab template
    /// (<c>ci/gitlab/nightowl.gitlab-ci.yml</c>): installs the release with its checksum-verifying install script, runs
    /// <c>nightowl scan</c> and publishes the native JSON report and SARIF as <c>artifacts.reports</c>, which GitBench reads
    /// into the repository's Security page, the pull request's Security tab and the Security merge check.
    /// Steps run in separate containers, so installing and scanning happen in one step. CI variables tune it:
    /// <c>NIGHTOWL_FAIL_ON</c> (critical|high|medium|low|none; empty uses <c>.nightowl/policy.yml</c>), <c>NIGHTOWL_PATH</c>,
    /// <c>NIGHTOWL_ARGS</c> (extra scan options, e.g. <c>--engines opengrep,gitleaks</c>), <c>NIGHTOWL_VERSION</c> and <c>NIGHTOWL_REPO</c>.
    /// Night Owl runs its engines from local binaries or pinned container images: the job needs either engines on PATH (a custom
    /// image) or access to a Docker daemon.
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
                apk add --no-cache curl git bash gcompat libstdc++ icu-libs >/dev/null
                export NIGHTOWL_HOME="$PWD/.nightowl-home" NIGHTOWL_INSTALL_DIR="$PWD/.nightowl-bin"
                curl -fsSL "https://raw.githubusercontent.com/${NIGHTOWL_REPO:-finepointcgi/Night-Owl}/main/scripts/install.sh" | sh
                failflag=""
                if [ -n "${NIGHTOWL_FAIL_ON:-}" ]; then failflag="--fail-on $NIGHTOWL_FAIL_ON"; fi
                "$NIGHTOWL_INSTALL_DIR/nightowl" scan "${NIGHTOWL_PATH:-.}" $failflag --format json --output nightowl.json \
                  --sarif-output nightowl.sarif --summary-output nightowl.md ${NIGHTOWL_ARGS:-}
        """;

    private static readonly Dictionary<string, string> All = new(StringComparer.OrdinalIgnoreCase) { ["nightowl"] = NightOwl };

    public static IReadOnlyCollection<string> Names => All.Keys;

    /// <summary>The YAML to paste into <c>.gitbench-ci.yml</c> under <c>jobs:</c> to customise a template.</summary>
    public static string? Snippet(string name) => All.TryGetValue(name, out var y) ? y : null;

    /// <summary>
    /// Adds the jobs of every template named in <paramref name="include"/> (a string or a list of strings) to
    /// <paramref name="jobs"/>, unless the file defines a job of that name. Returns an error message, or null.
    /// </summary>
    internal static string? Expand<TJob>(object? include, Dictionary<string, TJob?> jobs, Func<string, Dictionary<string, TJob?>> parse)
        where TJob : class
    {
        if (include == null) return null;
        var names = include switch
        {
            string s => [s],
            IEnumerable<object> list => list.Select(o => o as string).ToList(),
            _ => null,
        };
        if (names == null || names.Any(n => string.IsNullOrWhiteSpace(n)))
            return "'include' must be a template name or a list of names (e.g. include: [nightowl]).";
        foreach (var raw in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var name = raw!.Trim();
            if (!All.TryGetValue(name, out var yaml))
                return $"'include: {name}' is not a built-in template. Available: {string.Join(", ", All.Keys)}.";
            Dictionary<string, TJob?> templateJobs;
            try { templateJobs = parse(yaml); }
            catch (YamlException ex) { return $"built-in template '{name}' is invalid: {ex.Message}"; }
            foreach (var (jobName, job) in templateJobs)
                jobs.TryAdd(jobName, job); // the file's own job of that name wins
        }
        return null;
    }
}
