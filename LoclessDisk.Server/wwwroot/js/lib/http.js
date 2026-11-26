export async function requestJson(path, options) {
    const response = await fetch(path, options);
    const contentType = response.headers.get("content-type") ?? "";
    const result = contentType.includes("json") ? await response.json() : null;

    if (!response.ok) {
        throw new Error(result?.detail ?? result?.title ?? response.statusText);
    }
    return result;
}
