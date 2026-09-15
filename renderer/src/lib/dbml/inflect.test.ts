import { describe, expect, it } from "vitest";
import { genderEs, guessLanguage, nounFor, pluralize, singularize, splitWords } from "./inflect";

describe("splitWords", () => {
  it("splits snake, kebab, camel and Pascal case the same way", () => {
    for (const identifier of ["order_items", "order-items", "orderItems", "OrderItems", "Order Items"]) {
      expect(splitWords(identifier)).toEqual(["order", "items"]);
    }
  });
});

describe("Spanish", () => {
  it.each([
    ["usuarios", "usuario"],
    ["animales", "animal"],
    ["citas", "cita"],
    ["especies", "especie"],
    ["colores", "color"],
    ["ciudades", "ciudad"],
    ["leyes", "ley"],
    ["luces", "luz"],
    ["canciones", "canción"],
    // A `-e` singular takes only `-s`, even after a consonant cluster the other rule would cut.
    ["detalles", "detalle"],
    ["nombres", "nombre"],
    ["clientes", "cliente"],
    ["mensajes", "mensaje"],
    ["bases", "base"],
    ["lunes", "lunes"],
    ["usuario", "usuario"],
  ])("singularises %s as %s", (plural, singular) => {
    expect(singularize(plural, "es")).toBe(singular);
  });

  it.each([
    ["usuario", "usuarios"],
    ["animal", "animales"],
    ["luz", "luces"],
    ["canción", "canciones"],
    ["almacén", "almacenes"],
    ["direccion", "direcciones"],
    ["análisis", "análisis"],
  ])("pluralises %s as %s", (singular, plural) => {
    expect(pluralize(singular, "es")).toBe(plural);
  });

  it.each([
    ["usuario", "m"],
    ["animal", "m"],
    ["cita", "f"],
    ["vacuna", "f"],
    ["especie", "f"],
    ["canción", "f"],
    ["ciudad", "f"],
    ["sistema", "m"],
    ["día", "m"],
    ["mano", "f"],
  ] as const)("gives %s the gender %s", (word, gender) => {
    expect(genderEs(word)).toBe(gender);
  });
});

describe("English", () => {
  it.each([
    ["users", "user"],
    ["categories", "category"],
    ["addresses", "address"],
    ["boxes", "box"],
    ["matches", "match"],
    ["statuses", "status"],
    ["people", "person"],
    ["status", "status"],
    ["data", "data"],
    ["user", "user"],
  ])("singularises %s as %s", (plural, singular) => {
    expect(singularize(plural, "en")).toBe(singular);
  });

  it.each([
    ["user", "users"],
    ["category", "categories"],
    ["address", "addresses"],
    ["day", "days"],
    ["person", "people"],
  ])("pluralises %s as %s", (singular, plural) => {
    expect(pluralize(singular, "en")).toBe(plural);
  });
});

describe("nounFor", () => {
  it("reads the example the feature was asked for", () => {
    expect(nounFor("usuarios", "es")).toEqual({ singular: "usuario", plural: "usuarios", gender: "m" });
    expect(nounFor("animales", "es")).toEqual({ singular: "animal", plural: "animales", gender: "m" });
  });

  it("keeps a plural name's own words as its plural", () => {
    // Rebuilding it would give "animales vacuna", which nobody wrote.
    expect(nounFor("animal_vacunas", "es")).toEqual({ singular: "animal vacuna", plural: "animal vacunas", gender: "m" });
  });

  it("pluralises a singular name through its head: first word in Spanish, last in English", () => {
    expect(nounFor("linea_factura", "es").plural).toBe("lineas factura");
    expect(nounFor("orderItem", "en")).toEqual({ singular: "order item", plural: "order items", gender: "m" });
  });

  it("singularises only an English compound's head", () => {
    expect(nounFor("sales_orders", "en").singular).toBe("sales order");
  });
});

describe("guessLanguage", () => {
  it("recognises a schema named in Spanish", () => {
    expect(guessLanguage(["usuarios", "animales", "especies", "citas", "nombre", "creado_en"], "en")).toBe("es");
  });

  it("recognises a schema named in English", () => {
    expect(guessLanguage(["users", "posts", "comments", "created_at", "categories"], "es")).toBe("en");
  });

  it("falls back to the interface language when the names say nothing", () => {
    expect(guessLanguage(["t1", "t2", "id"], "es")).toBe("es");
    expect(guessLanguage([], "en")).toBe("en");
  });
});
