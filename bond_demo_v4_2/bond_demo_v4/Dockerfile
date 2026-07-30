FROM python:3.11-slim

ENV PYTHONDONTWRITEBYTECODE=1
ENV PYTHONUNBUFFERED=1

WORKDIR /app

COPY requirements-docker.txt /app/requirements-docker.txt
RUN pip install --no-cache-dir --upgrade pip
RUN pip install --no-cache-dir -r /app/requirements-docker.txt

COPY . /app

RUN chmod +x /app/start.sh

EXPOSE 8000 8050

CMD ["/app/start.sh"]
