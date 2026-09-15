/**
 * Just enough Spanish and English morphology to turn table names into nouns a sentence can use
 * (DBML-010): `usuarios` → "usuario", `order_items` → "order item", with a gender for Spanish articles.
 *
 * Deliberately a set of rules plus short exception lists, not a dictionary: table names are a
 * narrow vocabulary, and a wrong guess costs an awkward word in a tooltip, never a wrong diagram.
 * The rules are the ones that fit database vocabulary; each list says what it corrects.
 */

export type Lang = "es" | "en";
export type Gender = "m" | "f";

export interface Noun {
  /** For "each {singular}" — `usuario`, `order item`. */
  singular: string;
  /** For "many {plural}" — `usuarios`, `order items`. */
  plural: string;
  /** The head noun's grammatical gender. Only Spanish uses it; English sentences need no article agreement. */
  gender: Gender;
}

/** `orderItems`, `order_items`, `order-items` and `Order Items` all become `["order", "items"]`. */
export function splitWords(identifier: string): string[] {
  return identifier
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/([A-Z]+)([A-Z][a-z])/g, "$1 $2")
    .split(/[\s_\-.]+/)
    .map((word) => word.toLowerCase())
    .filter((word) => word.length > 0);
}

// ---------- Spanish ----------

const ES_VOWELS = "aeiouáéíóú";

/** Words whose singular and plural are the same. */
const ES_INVARIANT = new Set(["lunes", "martes", "miercoles", "miércoles", "jueves", "viernes", "crisis", "analisis", "análisis", "tesis", "dosis", "virus"]);

/**
 * Consonants a Spanish singular can end in after a vowel, which is when a plural took `-es` rather
 * than `-s`: `animal`es, `color`es, `ciudad`es, `plan`es, `ley`es. `detalles` (ll), `nombres` (br),
 * `clientes` (t) and `mensajes` (j) do not fit, so those took only `-s`.
 */
const ES_TAKES_ES = new Set(["l", "r", "n", "d", "y"]);

/** Feminine nouns the ending rules would call masculine. */
const ES_FEMININE = new Set([
  "mano", "foto", "moto", "radio", "especie", "clase", "base", "fase", "frase", "llave", "parte",
  "fuente", "calle", "noche", "nube", "carne", "leche", "suerte", "muerte", "gente", "sede", "red",
  "pared", "piel", "ley", "flor", "labor", "imagen", "orden", "tarde", "torre", "serie", "sangre",
  "madre", "costumbre", "sal", "miel",
]);

/** Masculine nouns the ending rules would call feminine. */
const ES_MASCULINE = new Set([
  "dia", "día", "mapa", "problema", "sistema", "tema", "idioma", "programa", "esquema", "clima",
  "planeta", "poema", "sofa", "sofá", "pijama", "dilema", "lema", "enigma", "diagrama", "analisis", "análisis",
]);

function singularEs(word: string): string {
  if (word.length <= 3 || ES_INVARIANT.has(word)) return word;
  if (word.endsWith("ces")) return `${word.slice(0, -3)}z`;
  if (word.endsWith("iones")) return `${word.slice(0, -5)}ión`;
  if (word.endsWith("es")) {
    const stem = word.slice(0, -2);
    if (ES_TAKES_ES.has(stem.at(-1) ?? "") && ES_VOWELS.includes(stem.at(-2) ?? " ")) return stem;
  }
  if (word.endsWith("s") && ES_VOWELS.includes(word.at(-2) ?? " ")) return word.slice(0, -1);
  return word;
}

function pluralEs(word: string): string {
  if (word.length === 0 || ES_INVARIANT.has(word)) return word;
  const last = word.at(-1) ?? "";
  if (last === "s" || last === "x") return word;
  if (last === "z") return `${word.slice(0, -1)}ces`;
  if (word.endsWith("ión")) return `${word.slice(0, -3)}iones`;
  if (ES_VOWELS.includes(last)) return `${word}s`;
  // `almacén` → `almacenes`: a stress mark on the final syllable falls away once a syllable is added.
  const unstressed = word.replace(/[áéíóú](?=[^aeiouáéíóú]*$)/, (vowel) => vowel.normalize("NFD").charAt(0));
  return `${unstressed}es`;
}

export function genderEs(word: string): Gender {
  if (ES_FEMININE.has(word)) return "f";
  if (ES_MASCULINE.has(word)) return "m";
  if (/(ión|ion|dad|tad|tud|umbre|eza|sis|itis)$/.test(word)) return "f";
  return word.endsWith("a") ? "f" : "m";
}

// ---------- English ----------

const EN_IRREGULAR: readonly (readonly [singular: string, plural: string])[] = [
  ["person", "people"], ["child", "children"], ["man", "men"], ["woman", "women"], ["foot", "feet"],
  ["tooth", "teeth"], ["mouse", "mice"], ["goose", "geese"], ["criterion", "criteria"],
  ["index", "indices"], ["matrix", "matrices"], ["vertex", "vertices"], ["leaf", "leaves"],
  ["half", "halves"], ["knife", "knives"], ["life", "lives"], ["wife", "wives"], ["shelf", "shelves"],
];
const EN_SINGULAR_OF = new Map(EN_IRREGULAR.map(([singular, plural]) => [plural, singular]));
const EN_PLURAL_OF = new Map(EN_IRREGULAR.map(([singular, plural]) => [singular, plural]));

/** Uncountable, or the same in both numbers. */
const EN_INVARIANT = new Set([
  "data", "metadata", "series", "species", "news", "status", "analysis", "sheep", "fish", "info",
  "information", "equipment", "software", "hardware", "feedback", "media", "staff",
]);

function singularEn(word: string): string {
  if (EN_INVARIANT.has(word)) return word;
  const irregular = EN_SINGULAR_OF.get(word);
  if (irregular) return irregular;
  if (word.length > 4 && word.endsWith("ies")) return `${word.slice(0, -3)}y`;
  if (/(ss|sh|ch|x|zz|us)es$/.test(word)) return word.slice(0, -2);
  if (/(ss|us|is)$/.test(word)) return word;
  if (word.length > 2 && word.endsWith("s")) return word.slice(0, -1);
  return word;
}

function pluralEn(word: string): string {
  if (EN_INVARIANT.has(word)) return word;
  const irregular = EN_PLURAL_OF.get(word);
  if (irregular) return irregular;
  if (/[^aeiou]y$/.test(word)) return `${word.slice(0, -1)}ies`;
  if (/(s|x|z|ch|sh)$/.test(word)) return `${word}es`;
  return `${word}s`;
}

// ---------- the public surface ----------

export function singularize(word: string, lang: Lang): string {
  return lang === "es" ? singularEs(word) : singularEn(word);
}

export function pluralize(word: string, lang: Lang): string {
  return lang === "es" ? pluralEs(word) : pluralEn(word);
}

/**
 * A table name as a noun.
 *
 * A name that already reads as plural keeps its own words as the plural — `animal_vacunas` stays
 * "animal vacunas" rather than being rebuilt as "animales vacuna". English inflects only the last
 * word, its head (`sales_orders` → "sales order"); Spanish compounds put the plural mark wherever
 * their author did, so every word is singularised and the first one carries the gender.
 */
export function nounFor(identifier: string, lang: Lang): Noun {
  const words = splitWords(identifier);
  if (words.length === 0) return { singular: identifier, plural: identifier, gender: "m" };

  const singularWords =
    lang === "es"
      ? words.map((word) => singularEs(word))
      : words.map((word, i) => (i === words.length - 1 ? singularEn(word) : word));

  const alreadyPlural = singularWords.some((word, i) => word !== words[i]);
  const headIndex = lang === "es" ? 0 : singularWords.length - 1;
  const pluralWords = alreadyPlural
    ? words
    : singularWords.map((word, i) => (i === headIndex ? pluralize(word, lang) : word));

  return {
    singular: singularWords.join(" "),
    plural: pluralWords.join(" "),
    gender: lang === "es" ? genderEs(singularWords[0] ?? "") : "m",
  };
}

// ---------- which language a schema is named in ----------

const ES_MARKERS = new Set([
  "usuario", "usuarios", "cliente", "clientes", "pedido", "pedidos", "producto", "productos",
  "factura", "facturas", "pago", "pagos", "nombre", "apellido", "fecha", "precio", "cantidad",
  "estado", "tipo", "codigo", "código", "descripcion", "descripción", "direccion", "dirección",
  "telefono", "teléfono", "correo", "creado", "actualizado", "eliminado", "activo", "empresa",
  "empresas", "categoria", "categorias", "categoría", "categorías", "detalle", "detalles", "cuenta",
  "cuentas", "rol", "roles", "permiso", "permisos", "animales", "especie", "especies", "cita", "citas",
  "vacuna", "vacunas", "de", "del", "en", "por", "para",
]);

const EN_MARKERS = new Set([
  "user", "users", "customer", "customers", "order", "orders", "product", "products", "invoice",
  "invoices", "payment", "payments", "name", "names", "date", "dates", "price", "prices", "quantity",
  "type", "types", "code", "codes", "description", "address", "addresses", "phone", "created",
  "updated", "deleted", "active", "company", "companies", "category", "categories", "detail",
  "details", "account", "accounts", "role", "permission", "permissions", "post", "posts", "comment",
  "comments", "tag", "tags", "item", "items", "at", "by", "of", "and", "is", "has", "with",
]);

/**
 * The language a schema's names are written in, scored over every identifier given; a tie — no
 * signal, or as much for one as for the other — falls back to the interface language.
 *
 * The inflection has to follow the *names*, not the interface: someone reading Spanish over a
 * schema named in English still needs `users` → "user", which Spanish rules would never produce.
 */
export function guessLanguage(identifiers: readonly string[], fallback: Lang): Lang {
  let es = 0;
  let en = 0;
  for (const identifier of identifiers) {
    for (const word of splitWords(identifier)) {
      if (/[ñáéíóúü]/.test(word)) es += 3;
      if (/(cion|ción|ciones|dad|dades)$/.test(word)) es += 2;
      if (ES_MARKERS.has(word)) es += 2;
      if (EN_MARKERS.has(word)) en += 2;
      if (/(ies|ing|ness|ment|ship)$/.test(word)) en += 2;
      if (/(th|w|k)/.test(word)) en += 1;
    }
  }
  if (es === en) return fallback;
  return es > en ? "es" : "en";
}
