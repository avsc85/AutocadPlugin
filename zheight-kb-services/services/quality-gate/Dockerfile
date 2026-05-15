FROM python:3.12-slim

WORKDIR /app

COPY shared/ /app/shared/

COPY services/quality-gate/requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY services/quality-gate/ /app/service/

ENV PYTHONPATH=/app

CMD ["uvicorn", "service.app.main:app", "--host", "0.0.0.0", "--port", "8080", "--workers", "1"]
