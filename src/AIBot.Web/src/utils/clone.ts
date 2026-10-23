/** Clone JSON API data, including Vue's reactive proxies. */
export function cloneJson<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T
}
