from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse
from pydantic import BaseModel
from sentence_transformers import SentenceTransformer
import uvicorn

app = FastAPI()

# Carica il modello una sola volta
model = SentenceTransformer('sentence-transformers/all-MiniLM-L6-v2')  # puoi usare lo stesso per entrambi

class InputText(BaseModel):
    testo: str

@app.post("/embedding")
async def calcola_embedding(input: InputText):
    embedding = model.encode(input.testo).tolist()
    return {"embedding": embedding}

@app.post("/embedding-multiplo")
async def embedding_multiplo(request: Request):
    try:
        payload = await request.json()
        testi = payload.get("testi", [])
        if not testi or not isinstance(testi, list):
            return JSONResponse(status_code=400, content={"errore": "Campo 'testi' mancante o non valido"})

        embeddings = model.encode(testi).tolist()  # uso lo stesso modello
        return {"embeddings": embeddings}
    except Exception as e:
        return JSONResponse(status_code=500, content={"errore": str(e)})

if __name__ == "__main__":
    uvicorn.run(app, host="127.0.0.1", port=8000)  # <-- porta corretta per il backend C#

