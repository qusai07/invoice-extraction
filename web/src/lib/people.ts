/**
 * How to show "who did it". The API stores user ids; it sends display names alongside them.
 * Fallbacks: the automatic pipeline ("system") gets a translated label; anything else unknown
 * (a deleted user, a development header name) is shown as stored.
 */
export function personLabel(id: string | null | undefined, name: string | null | undefined, t: (key: "who.system") => string): string {
  if (name) return name;
  if (!id) return "—";
  if (id === "system") return t("who.system");
  return id;
}
