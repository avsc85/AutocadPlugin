FROM python:3.12-slim

WORKDIR /app

COPY shared/ /app/shared/

COPY services/rag-api/requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY services/rag-api/ /app/service/

ENV PYTHONPATH=/app

CMD ["uvicorn", "service.app.main:app", "--host", "0.0.0.0", "--port", "8080", "--workers", "2"]
