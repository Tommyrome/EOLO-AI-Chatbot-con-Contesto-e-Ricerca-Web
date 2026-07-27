import sys
import json
import random
from qdrant_client import QdrantClient
from qdrant_client.models import Distance, VectorParams
from sentence_transformers import SentenceTransformer, CrossEncoder

# Modelli: bi-encoder per embedding, cross-encoder per re-ranking
bi_encoder = SentenceTransformer("sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2")
cross_encoder_model = CrossEncoder("cross-encoder/ms-marco-MiniLM-L-6-v2")

class QdrantService:
    def __init__(self, url: str, api_key: str, collection_name: str = "chat_embeddings", vector_size: int = 384):
        self.client = QdrantClient(url=url, api_key=api_key)
        self.collection_name = collection_name
        self.vector_size = vector_size

    def create_collection(self):
        if not self.client.collection_exists(self.collection_name):
            self.client.create_collection(
                collection_name=self.collection_name,
                vectors_config=VectorParams(size=self.vector_size, distance=Distance.COSINE)
            )
            print(f"Collezione '{self.collection_name}' creata con vettori di dimensione {self.vector_size}.", file=sys.stderr)
        else:
            print(f"Collezione '{self.collection_name}' esiste già.", file=sys.stderr)


    def upsert_embedding(self, id: int, embedding: list[float], payload: dict):
        self.client.upsert(
            collection_name=self.collection_name,
            points=[{
                "id": id,
                "vector": embedding,
                "payload": payload
            }]
        )
        print(f"Embedding con id {id} inserito/aggiornato. (qdrant_service)", file=sys.stderr)

    def search(self, embedding, query_text=None, top_k=10, score_threshold=0.4, re_rank_model=None, metadata_filter=None):
        try:
            results = self.client.search(
                collection_name=self.collection_name,
                query_vector=embedding,
                limit=top_k * 2,
                score_threshold=score_threshold,
                with_vectors=True,
                with_payload=True
            )
        except Exception as e:
            error_response = {"error": f"Errore durante la ricerca su Qdrant: {str(e)}"}
            print(json.dumps(error_response))  # qui va bene stampare la risposta di errore JSON
            sys.exit(1)

        # Log su stderr (non stdout)
        sys.stderr.write(f"-(Risultati che devono essere classificati) Totale risultati Qdrant: {len(results)}\n")
        for i, r in enumerate(results):
            testo = r.payload.get("text", "[n/d]")
            score = getattr(r, "score", 0)
            sys.stderr.write(f"Risultato #{i+1}: score={score:.4f}, testo={testo[:80]}...\n")

        if metadata_filter:
            results = [r for r in results if metadata_filter(r.payload)]

        candidates = []
        for r in results:
            testo = r.payload.get("text")
            vettore = getattr(r, "vector", None)
            score = getattr(r, "score", 0)
            if testo and vettore:
                candidates.append({
                    "testo": testo,
                    "embedding": vettore,
                    "score": score
                })

        sys.stderr.write(f"-(primo ranking quello byencoder, fa embedding) Candidati per ranking (count={len(candidates)}):\n")
        for c in candidates:
            sys.stderr.write(f"score={c['score']:.4f}, testo={c['testo'][:80]}...\n")

        if re_rank_model and query_text is not None:
            ranked_candidates = re_rank_model.rank(query_text=query_text, candidates=candidates)
            sys.stderr.write("-(secondo ranking fa re-ranking) Ranking con Cross-Encoder:\n")
            for c in ranked_candidates:
                sys.stderr.write(f"score={c['score']:.4f}, testo={c['testo'][:80]}...\n")
        else:
            ranked_candidates = sorted(candidates, key=lambda x: x["score"], reverse=True)

        top_results = ranked_candidates[:top_k]

        sys.stderr.write(f"-(i risultati più compatibili dopo essere stai classificati) Qdrant search: risultati trovati {len(top_results)} (top_k={top_k}, soglia={score_threshold})\n")
        for i, result in enumerate(top_results, 1):
            payload = result["testo"]  # o result["embedding"] o qualunque cosa ti serve
            score = result["score"]
            sys.stderr.write(f" → [{i}] Score: {score:.4f} | Testo: {payload[:80]}...\n")
        

        response = [{"testo": c["testo"], "embedding": c["embedding"]} for c in top_results]

        # Stampo SOLO la risposta JSON pulita
        print(json.dumps(response))
        return response

class CrossEncoderReRanker:
    def __init__(self, model):
        self.model = model

    def rank(self, query_text, candidates):
        # Prepara coppie (query testuale, testo candidato)
        pairs = [(query_text, c["testo"]) for c in candidates]
        scores = self.model.predict(pairs)

        # Aggiorna punteggio candidati con score del Cross-Encoder
        for i, c in enumerate(candidates):
            c["score"] = float(scores[i])

        # Ordina per score decrescente e ritorna
        return sorted(candidates, key=lambda x: x["score"], reverse=True)

def main():
    URL = "https://25cfc2ae-2333-45f0-8510-505468b1fbaf.europe-west3-0.gcp.cloud.qdrant.io"
    API_KEY = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJhY2Nlc3MiOiJtIn0.nE2N9Aps9l_opuqHHxNOC4RgQpGfV4JTQVVqbQ-cp_k"

    qdrant = QdrantService(URL, API_KEY)
    qdrant.create_collection()

    re_ranker = CrossEncoderReRanker(cross_encoder_model)

    if len(sys.argv) < 2:
        print(json.dumps({"error": "Parametri insufficienti. Usa: insert o search"}))
        sys.exit(1)

    command = sys.argv[1].lower()

    if command == "insert":
        print("➡️ Comando INSERT ricevuto (qdrant_service)", file=sys.stderr)
        if len(sys.argv) != 4:
            print(json.dumps({"error": "Per 'insert' servono 2 argomenti: '[embedding]' e '{payload}'"}))
            sys.exit(1)

        try:
            embedding = json.loads(sys.argv[2])
            payload = json.loads(sys.argv[3])
        except json.JSONDecodeError as e:
            print(json.dumps({"error": f"Errore parsing JSON input: {str(e)}"}))
            sys.exit(1)

        random_id = random.randint(100000, 999999)
        qdrant.upsert_embedding(random_id, embedding, payload)

    elif command == "search":
        if len(sys.argv) < 4:
            print(json.dumps({"error": "Per 'search' servono 2 argomenti: '[embedding]' e 'query_text'"}))
            sys.exit(1)

        try:
            embedding = json.loads(sys.argv[2])
            query_text = sys.argv[3]
        except json.JSONDecodeError as e:
            print(json.dumps({"error": f"Errore parsing embedding JSON: {str(e)}"}))
            sys.exit(1)

        # Passo anche il testo della query per il re-ranking
        qdrant.search(embedding, query_text=query_text, re_rank_model=re_ranker)

    else:
        print(json.dumps({"error": f"Comando sconosciuto '{command}'. Usa 'insert' o 'search'"}))
        sys.exit(1)

if __name__ == "__main__":
    main()