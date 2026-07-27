from datasets import load_dataset
from sentence_transformers import SentenceTransformer
from qdrant_client import QdrantClient
from qdrant_client.models import VectorParams, Distance, PointStruct
from tqdm import tqdm
import os
from time import time

# Configura modello embedding e Qdrant
model = SentenceTransformer('sentence-transformers/all-MiniLM-L6-v2')
dataset = load_dataset("PleIAs/common_corpus", split="train[:5GB]")  # Cambia qui il dataset

# Estrai i testi (usa 'text' o 'prompt' a seconda del dataset)
train_data = dataset["train"]
prompts = [item["text"] if "text" in item else item.get("prompt", "") for item in train_data]

qdrant = QdrantClient(
    url="https://25cfc2ae-2333-45f0-8510-505468b1fbaf.europe-west3-0.gcp.cloud.qdrant.io",
    api_key="eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJhY2Nlc3MiOiJtIn0.nE2N9Aps9l_opuqHHxNOC4RgQpGfV4JTQVVqbQ-cp_k"
)

collection_name = "chat_embeddings"
vector_size = model.get_sentence_embedding_dimension()

# Crea collezione se non esiste
if not qdrant.collection_exists(collection_name):
    qdrant.create_collection(
        collection_name=collection_name,
        vectors_config=VectorParams(size=vector_size, distance=Distance.COSINE)
    )

# Carica ID inseriti
inseriti_file = "inseriti2.txt"
id_gia_inseriti = set()
if os.path.exists(inseriti_file):
    with open(inseriti_file, "r") as f:
        id_gia_inseriti = set(int(line.strip()) for line in f)

# Filtra nuovi dati da inserire
nuovi = [(i, prompt) for i, prompt in enumerate(prompts) if i not in id_gia_inseriti and prompt.strip()]

# Inserimento batch con stima ETA
batch_size = 32
progress_bar = tqdm(
    range(0, len(nuovi), batch_size),
    desc="Inserimento batch",
    unit="batch",
    dynamic_ncols=True
)
start_time = time()

for i in progress_bar:
    batch = nuovi[i:i+batch_size]
    ids = [item[0] for item in batch]
    texts = [item[1] for item in batch]

    embeddings = model.encode(texts, show_progress_bar=False).tolist()
    punti = [
        PointStruct(id=id_, vector=vec, payload={"text": txt})
        for id_, vec, txt in zip(ids, embeddings, texts)
    ]

    qdrant.upsert(collection_name=collection_name, points=punti)

    # Aggiorna file degli ID inseriti
    with open(inseriti_file, "a") as f:
        for id_ in ids:
            f.write(f"{id_}\n")

    # Calcola ETA
    elapsed = time() - start_time
    processed = i + batch_size
    remaining = len(nuovi) - processed
    speed = processed / elapsed
    eta = remaining / speed if speed > 0 else 0
    progress_bar.set_postfix_str(f"ETA: {int(eta // 60)}m {int(eta % 60)}s")