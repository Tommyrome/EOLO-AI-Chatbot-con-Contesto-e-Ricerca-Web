from sklearn.metrics.pairwise import cosine_similarity
from serpapi import GoogleSearch
import sys
import json
import requests

sys.stderr.reconfigure(encoding='utf-8')

SERPAPI_API_KEY = "10786895b7c87ed952ea4efb64b350bd0421b51053f1f50f6a3513dc0249635b"
SERPAPI_BACKUP_KEY = "846d7474852d465d844069b69edf646fc503fe2a949a8596a65fedf55ade8c85"

def effettua_ricerca(query, num_risultati, api_key):
    search = GoogleSearch({
        "q": query,
        "api_key": api_key,
        "num": num_risultati,
        "hl": "it",
        "gl": "it"
    })
    return search.get_dict()

def cerca_online(query, num_risultati=100):
    try:
        results = effettua_ricerca(query, num_risultati, SERPAPI_API_KEY)
        if "error" in results:
            raise Exception(f"Errore con chiave principale: {results['error']}")
    except Exception as e1:
        print(f"⚠️ Errore con API Key principale: {e1}\n➡️ Provo con chiave di backup...", file=sys.stderr)
        try:
            results = effettua_ricerca(query, num_risultati, SERPAPI_BACKUP_KEY)
            if "error" in results:
                raise Exception(f"Errore anche con chiave backup: {results['error']}")
        except Exception as e2:
            print(f"❌ Errore con chiave di backup: {e2}", file=sys.stderr)
            return []

    snippet_list = []
    for result in results.get("organic_results", [])[:num_risultati]:
        titolo = result.get("title", "")
        snippet = result.get("snippet", "")
        link = result.get("link", "")
        if snippet:
            snippet_list.append({
                "titolo": titolo,
                "snippet": snippet,
                "link": link
            })
    return snippet_list

def main():
    if len(sys.argv) < 4:
        print("Uso: SerpAPI.py search <embedding_json> <testo_query>", file=sys.stderr)
        sys.exit(1)

    embedding_query_json = sys.argv[2]
    testo_query = sys.argv[3]

    try:
        embedding_query = json.loads(embedding_query_json)
    except Exception as e:
        print(f"Errore nel parsing JSON dell'embedding_query: {e}", file=sys.stderr)
        sys.exit(1)

    if not embedding_query or len(embedding_query) == 0:
        print("Errore: embedding_query è vuoto", file=sys.stderr)
        sys.exit(1)

    if isinstance(embedding_query[0], float):
        embedding_query = [embedding_query]

    snippets = cerca_online(testo_query, num_risultati=10)

    if not snippets:
        print("⚠️ Nessuno snippet trovato - fallback Qdrant.", file=sys.stderr)
        sys.exit(1)

    testi = [s["snippet"] for s in snippets if s.get("snippet")]
    if not testi:
        print("⚠️ Nessuno snippet utile (campo 'snippet' vuoto) - fallback Qdrant.", file=sys.stderr)
        sys.exit(1)

    print("\n[LOG] --- INIZIO SNIPPET DA CLASSIFICARE ---", file=sys.stderr)
    for i, s in enumerate(snippets):
        print(f"[{i+1}] Titolo: {s['titolo']}\nSnippet: {s['snippet']}\n", file=sys.stderr)
    print("[LOG] --- FINE SNIPPET DA CLASSIFICARE ---\n", file=sys.stderr)

    # --- CHIAMATA AL SERVER FASTAPI PER GLI EMBEDDING ---
    try:
        response = requests.post("http://localhost:8000/embedding-multiplo", json={"testi": testi})
        if not response.ok:
            print("Errore da /embedding-multiplo: " + response.text, file=sys.stderr)
            sys.exit(1)

        embedding_snippet = response.json().get("embeddings")
        if not embedding_snippet or len(embedding_snippet) == 0:
            print("⚠️ embedding_snippet vuoto - fallback Qdrant.", file=sys.stderr)
            sys.exit(1)

        if isinstance(embedding_snippet[0], float):
            embedding_snippet = [embedding_snippet]

        scores = cosine_similarity(embedding_query, embedding_snippet)[0]
    except Exception as e:
        print(f"❌ Errore durante la richiesta embedding-multiplo: {e}", file=sys.stderr)
        sys.exit(1)

    # Ordina top 5
    ranked = sorted(zip(snippets, scores), key=lambda x: x[1], reverse=True)[:5]

    print("[LOG] --- TOP 5 SNIPPET SELEZIONATI ---", file=sys.stderr)
    for i, (item, score) in enumerate(ranked):
        print(f"[TOP {i+1}] Similarità: {score:.4f}\nSnippet: {item['snippet']}\n", file=sys.stderr)
    print("[LOG] --- FINE TOP 5 ---\n", file=sys.stderr)

    output = []
    for item, score in ranked:
        snippet_text = item["snippet"]
        idx = testi.index(snippet_text)
        embedding = embedding_snippet[idx]
        output.append({
            "testo": snippet_text,
            "embedding": embedding
        })

    sys.stdout.buffer.write(json.dumps(output, ensure_ascii=False).encode('utf-8'))
    sys.stdout.flush()

if __name__ == "__main__":
    main()