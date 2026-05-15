FROM python:3.12-slim

WORKDIR /app

# System deps for ezdxf, PyMuPDF, and optional LibreCAD
RUN apt-get update && apt-get install -y --no-install-recommends \
    libglib2.0-0 libsm6 libxext6 libxrender-dev \
    && rm -rf /var/lib/apt/lists/*

# Copy shared layer first (changes rarely — better layer caching)
COPY shared/ /app/shared/

# Install dependencies
COPY services/ingestion/requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Copy service source
COPY services/ingestion/ /app/service/

# Make shared importable
ENV PYTHONPATH=/app

CMD ["uvicorn", "service.app.main:app", "--host", "0.0.0.0", "--port", "8080", "--workers", "1"]
