export function sessionKey(server: string, game: string, npc: string, player: string) {
  const url = new URL(server.trim() || location.origin, location.origin)
  const base = `${url.origin}${url.pathname.replace(/\/+$/, '')}`
  return `aibot.debug.session.v2.${JSON.stringify([base, game, npc, player])}`
}
