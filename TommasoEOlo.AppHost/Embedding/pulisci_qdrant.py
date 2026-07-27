from qdrant_client import QdrantClient
from qdrant_client.models import PointIdsList

# Configura questi parametri secondo il tuo ambiente
QDRANT_URL = "https://25cfc2ae-2333-45f0-8510-505468b1fbaf.europe-west3-0.gcp.cloud.qdrant.io"
API_KEY = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJhY2Nlc3MiOiJtIn0.nE2N9Aps9l_opuqHHxNOC4RgQpGfV4JTQVVqbQ-cp_k"
COLLECTION = "chat_embeddings"

# Connessione a Qdrant
client = QdrantClient(url=QDRANT_URL, api_key=API_KEY)

print("Recupero tutti i punti nella collection...")

# Ottieni tutti gli ID dei punti in batch
all_point_ids = []
offset = 0
batch_size = 1000  # o meno se vuoi

while True:
    points, _ = client.scroll(
        collection_name=COLLECTION,
        limit=batch_size,
        offset=offset,
        with_payload=False,
        with_vectors=False
    )
    if not points:
        break

    all_point_ids.extend([point.id for point in points])
    offset += batch_size

print(f"Trovati {len(all_point_ids)} punti. Procedo con la cancellazione...")

# Cancella i punti in batch
for i in range(0, len(all_point_ids), batch_size):
    batch_ids = all_point_ids[i:i+batch_size]
    client.delete(
        collection_name=COLLECTION,
        points_selector=PointIdsList(points=batch_ids)
    )
    print(f"Cancellati punti {i} - {i + len(batch_ids) - 1}")

print("Pulizia completa: tutti i punti sono stati rimossi dalla collection.")