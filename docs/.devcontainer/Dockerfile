FROM ubuntu:20.04

WORKDIR /home/

COPY . .

ENV DEBIAN_FRONTEND=noninteractive 
RUN bash ./setup.sh

RUN echo 'export NVM_DIR="$HOME/.nvm"' >> "$HOME/.zshrc"
RUN echo '\n' >> "$HOME/.zshrc"
