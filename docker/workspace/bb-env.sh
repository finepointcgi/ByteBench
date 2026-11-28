# Sourced by non-interactive bash (BASH_ENV) so `docker exec ... bash -c`, scripts and tasks see the environment ByteBench injected
# (repository variables, secrets) in ~/.bb-env. Mode 600 in the developer's home, so for every other user (bbai, the collaborators) this is a no-op.
# ~/.bb-env guards itself against being loaded twice (BB_ENV_LOADED), so sourcing it again from ~/.bashrc / ~/.profile is harmless.
if [ -z "${BB_ENV_LOADED:-}" ] && [ -r "${HOME:-/nonexistent}/.bb-env" ]; then . "${HOME}/.bb-env"; fi
